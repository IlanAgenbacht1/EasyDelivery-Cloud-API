using Google.Cloud.Firestore;
using DeliveryCloudApi.Services;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using DeliveryCloudApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure Firestore
var projectId = builder.Configuration["GoogleCloud:FirestoreProjectId"] ?? 
               Environment.GetEnvironmentVariable("FIRESTORE_PROJECT_ID") ?? 
               "easydelivery-433910";

builder.Services.AddSingleton(provider => 
{
    var firestoreDb = FirestoreDb.Create(projectId);
    return firestoreDb;
});

// Add HttpClient for geocoding
builder.Services.AddHttpClient();

// Register our services
builder.Services.AddScoped<IFirestoreService, FirestoreService>();
builder.Services.AddScoped<IClientValidationService, ClientValidationService>();
builder.Services.AddScoped<ITransformationService, TransformationService>();
builder.Services.AddScoped<IGeocodingService, GeocodingService>();
builder.Services.AddScoped<IPodPdfService, PodPdfService>();
builder.Services.AddScoped<IEmailService, EmailService>();

// Add logging
builder.Logging.AddConsole();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Add controller routing
app.MapControllers();

// Health check endpoint
app.MapGet("/", () => "Delivery Cloud API - Running on Cloud Run");

app.MapGet("/health", () => new { status = "healthy", timestamp = DateTime.UtcNow });

// Receive Shipments endpoint - for push agents to send data
app.MapPost("/receive-shipments", async (
    [FromBody] ShipmentPushRequest request,
    ILogger<Program> logger,
    IFirestoreService firestoreService,
    IClientValidationService clientValidationService,
    ITransformationService transformationService) =>
{
    logger.LogInformation("Received shipments from client {ClientId} at {Time}", request.ClientId, DateTime.UtcNow);

    try
    {
        // 1. Validate client exists and is active
        // Note: Authentication is now handled by Google Cloud at the infrastructure level
        // We only need to validate that the client exists and is active
        var client = await clientValidationService.ValidateClientAsync(request.ClientId);
        if (client == null)
        {
            logger.LogWarning("Invalid or inactive client: {ClientId}", request.ClientId);
            return Results.BadRequest(new { error = "Invalid or inactive client", clientId = request.ClientId });
        }

        var totalShipments = request.Shipments?.Count ?? 0;
        var totalTrips = 0;
        var newTripsCreated = 0;
        var existingTripsUsed = 0;
        var skippedCount = 0;
        var newShipments = new List<SageShipmentHeader>();
        var successfullyProcessedShipments = new List<SageShipmentHeader>();
        var failedShipments = new List<(SageShipmentHeader shipment, string error)>();

        logger.LogInformation("Processing {ShipmentCount} shipments from client {ClientId}", totalShipments, request.ClientId);

        if (totalShipments > 0)
        {
            // 2. Check for duplicate shipments before processing

            foreach (var shipment in request.Shipments!)
            {
                if (await firestoreService.IsShipmentProcessedAsync(request.ClientId, shipment.SHIUNIQ))
                {
                    logger.LogDebug("Skipping already processed shipment: ClientId={ClientId}, SHIUNIQ={SHIUNIQ}", 
                        request.ClientId, shipment.SHIUNIQ);
                    skippedCount++;
                }
                else
                {
                    newShipments.Add(shipment);
                }
            }

            logger.LogInformation("Shipment processing: {NewCount} new, {SkippedCount} already processed", 
                newShipments.Count, skippedCount);

            if (newShipments.Count > 0)
            {
                // 3. Transform only new shipments to trips/deliveries
                var trips = await transformationService.ConvertShipmentsToTripsAsync(request.ClientId, newShipments);
                totalTrips = trips.Count;

                // 4. Save trips and their associated deliveries to Firestore
                
                foreach (var (tripId, trip) in trips)
                {
                    try
                    {
                        // Check if trip already exists (handle short shipments/partial deliveries)
                        var existingTrip = await firestoreService.GetTripAsync(tripId);
                        if (existingTrip != null)
                        {
                            logger.LogInformation("Trip {TripId} already exists (short shipment scenario). Status: {Status}, Created: {CreatedAt}", 
                                tripId, existingTrip.Status, existingTrip.CreatedAt);
                                
                            // Handle trip status based on business rules for short shipments
                            await HandleExistingTripStatusAsync(firestoreService, tripId, existingTrip, logger);
                            existingTripsUsed++;
                        }
                        else
                        {
                            // New trip - save it
                            await firestoreService.SaveTripAsync(tripId, trip);
                            logger.LogDebug("Created new trip {TripId} for client {ClientId}", tripId, trip.ClientId);
                            newTripsCreated++;
                        }

                        // Save deliveries only for shipments that belong to this specific trip
                        var shipmentsByTrip = GroupShipmentsByTrip(newShipments);
                        if (shipmentsByTrip.TryGetValue(tripId, out var tripShipments))
                        {
                            foreach (var shipment in tripShipments)
                            {
                                try
                                {
                                    // Check if delivery already exists (important for short shipments)
                                    var existingDelivery = await firestoreService.GetDeliveryAsync(tripId, shipment.SHINUMBER);
                                    if (existingDelivery != null)
                                    {
                                        logger.LogWarning("Delivery {SHINUMBER} already exists under trip {TripId} - this may indicate a duplicate shipment or data issue", 
                                            shipment.SHINUMBER, tripId);
                                        // Skip creating duplicate delivery
                                        successfullyProcessedShipments.Add(shipment);
                                        continue;
                                    }
                                    
                                    var deliveries = await transformationService.ConvertShipmentToDeliveriesAsync(shipment);
                                    foreach (var delivery in deliveries)
                                    {
                                        // Use SHINUMBER as delivery ID for uniqueness across the system
                                        await firestoreService.SaveDeliveryAsync(tripId, shipment.SHINUMBER, delivery);
                                        logger.LogDebug("Saved delivery {SHINUMBER} under trip {TripId} for order {OrderNumber}", 
                                            shipment.SHINUMBER, tripId, shipment.ORDNUMBER);
                                    }
                                    successfullyProcessedShipments.Add(shipment);
                                }
                                catch (Exception shipmentEx)
                                {
                                    logger.LogError(shipmentEx, "Failed to process shipment {SHIUNIQ} (SHINUMBER: {SHINUMBER}) for trip {TripId}", 
                                        shipment.SHIUNIQ, shipment.SHINUMBER, tripId);
                                    failedShipments.Add((shipment, shipmentEx.Message));
                                }
                            }
                        }
                    }
                    catch (Exception tripEx)
                    {
                        logger.LogError(tripEx, "Failed to save trip {TripId}", tripId);
                        // Mark all shipments for this trip as failed
                        var shipmentsByTrip = GroupShipmentsByTrip(newShipments);
                        if (shipmentsByTrip.TryGetValue(tripId, out var tripShipments))
                        {
                            foreach (var shipment in tripShipments)
                            {
                                failedShipments.Add((shipment, $"Trip save failed: {tripEx.Message}"));
                            }
                        }
                    }
                }

                // 5. Mark only successfully processed shipments as processed
                foreach (var shipment in successfullyProcessedShipments)
                {
                    try
                    {
                        var processedShipment = new ProcessedShipment
                        {
                            ClientId = request.ClientId,
                            SHIUNIQ = shipment.SHIUNIQ,
                            ORDNUMBER = shipment.ORDNUMBER,
                            SHINUMBER = shipment.SHINUMBER,
                            ShipmentDate = ConvertSageDate(shipment.SHIDATE),
                            ProcessedDate = DateTime.UtcNow
                        };

                        await firestoreService.MarkShipmentProcessedAsync(processedShipment);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to mark shipment {SHIUNIQ} as processed", shipment.SHIUNIQ);
                        // Don't fail the entire batch for marking issues
                    }
                }
                
                // Log summary of processing results
                if (failedShipments.Any())
                {
                    logger.LogWarning("Batch processing completed with {SuccessCount} successful and {FailCount} failed shipments", 
                        successfullyProcessedShipments.Count, failedShipments.Count);
                    
                    foreach (var (failedShipment, error) in failedShipments.Take(5)) // Log first 5 failures
                    {
                        logger.LogWarning("Failed shipment {SHIUNIQ}: {Error}", failedShipment.SHIUNIQ, error);
                    }
                }
            }
        }

        var result = new
        {
            success = true,
            message = newShipments.Count > 0 
                ? $"Batch completed: {successfullyProcessedShipments?.Count ?? 0} successful, {failedShipments?.Count ?? 0} failed. Trips: {newTripsCreated} new, {existingTripsUsed} existing (short shipments)."
                : $"No new shipments to process. {skippedCount} already processed.",
            totalShipmentsReceived = totalShipments,
            newShipments = newShipments.Count,
            skippedShipments = skippedCount,
            successfullyProcessed = successfullyProcessedShipments?.Count ?? 0,
            failedShipments = failedShipments?.Count ?? 0,
            clientId = request.ClientId,
            tripsCreated = newTripsCreated,
            existingTripsUsed = existingTripsUsed,
            totalTripsInvolved = newTripsCreated + existingTripsUsed,
            timestamp = DateTime.UtcNow
        };

        logger.LogInformation(result.message);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error processing shipments from client {ClientId}", request.ClientId);
        return Results.Problem($"Error: {ex.Message}", statusCode: 500);
    }
});

// Update Delivery Status endpoint - equivalent to UpdateDeliveryStatusFunction
app.MapPost("/update-delivery-status", async (
    [FromBody] DeliveryStatusUpdateRequest updateRequest,
    ILogger<Program> logger,
    IFirestoreService firestoreService) =>
{
    logger.LogInformation("UpdateDeliveryStatus endpoint triggered at {Time}", DateTime.UtcNow);

    try
    {
        if (updateRequest == null || string.IsNullOrEmpty(updateRequest.TripId) || string.IsNullOrEmpty(updateRequest.DeliveryId))
        {
            return Results.BadRequest("TripId and DeliveryId are required");
        }

        logger.LogInformation("Updating delivery status: TripId={TripId}, DeliveryId={DeliveryId}, Status={Status}", 
            updateRequest.TripId, updateRequest.DeliveryId, updateRequest.Status);

        // Validate status value
        if (updateRequest.Status != "pending" && updateRequest.Status != "completed")
        {
            return Results.BadRequest(new { error = "Status must be 'pending' or 'completed'", providedStatus = updateRequest.Status });
        }

        // Check if delivery exists before attempting update
        var existingDelivery = await firestoreService.GetDeliveryAsync(updateRequest.TripId, updateRequest.DeliveryId);
        if (existingDelivery == null)
        {
            return Results.NotFound(new { error = "Delivery not found", tripId = updateRequest.TripId, deliveryId = updateRequest.DeliveryId });
        }

        // Update the delivery status and POD data
        await firestoreService.UpdateDeliveryStatusAsync(
            updateRequest.TripId, 
            updateRequest.DeliveryId, 
            updateRequest.Status, 
            updateRequest.PodData);

        var response = new
        {
            success = true,
            message = "Delivery status updated successfully",
            tripId = updateRequest.TripId,
            deliveryId = updateRequest.DeliveryId,
            previousStatus = existingDelivery.Status,
            newStatus = updateRequest.Status,
            podDataUpdated = !string.IsNullOrWhiteSpace(updateRequest.PodData),
            updatedAt = DateTime.UtcNow
        };

        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error in UpdateDeliveryStatus endpoint");
        return Results.Problem($"Error: {ex.Message}", statusCode: 500);
    }
});


app.Run();

// Helper method to handle existing trip status for short shipments
static async Task HandleExistingTripStatusAsync(
    IFirestoreService firestoreService, 
    string tripId, 
    Trip existingTrip, 
    ILogger<Program> logger)
{
    switch (existingTrip.Status.ToLower())
    {
        case "completed":
            // Trip was completed but now has additional shipments - reopen it
            logger.LogInformation("Reopening completed trip {TripId} due to short shipment arrival", tripId);
            await firestoreService.UpdateTripStatusAsync(tripId, "available", null, null);
            break;
            
        case "available":
        case "claimed":
        case "in_progress":
            // These statuses are fine for receiving additional deliveries
            logger.LogDebug("Trip {TripId} status '{Status}' allows additional deliveries", tripId, existingTrip.Status);
            break;
            
        default:
            // Unknown status - log warning but don't change it
            logger.LogWarning("Trip {TripId} has unknown status '{Status}' - preserving as-is", tripId, existingTrip.Status);
            break;
    }
}

// Helper method to group shipments by their trip ID
static Dictionary<string, List<SageShipmentHeader>> GroupShipmentsByTrip(List<SageShipmentHeader> shipments)
{
    var shipmentsByTrip = new Dictionary<string, List<SageShipmentHeader>>();
    
    foreach (var shipment in shipments)
    {
        var orderNumber = shipment.ORDNUMBER.Trim();
        if (string.IsNullOrEmpty(orderNumber))
            continue;
            
        // Use same logic as TransformationService: prioritize ROUTE, fall back to order number
        var tripId = !string.IsNullOrEmpty(shipment.ROUTE) ? shipment.ROUTE.Trim() : orderNumber;
        
        if (!shipmentsByTrip.ContainsKey(tripId))
        {
            shipmentsByTrip[tripId] = new List<SageShipmentHeader>();
        }
        
        shipmentsByTrip[tripId].Add(shipment);
    }
    
    return shipmentsByTrip;
}

// Helper method for converting Sage date format
static DateTime ConvertSageDate(decimal sageDate)
{
    if (sageDate == 0) return DateTime.MinValue;

    try
    {
        var dateStr = ((long)sageDate).ToString("00000000");
        if (dateStr.Length == 8)
        {
            var year = int.Parse(dateStr.Substring(0, 4));
            var month = int.Parse(dateStr.Substring(4, 2));
            var day = int.Parse(dateStr.Substring(6, 2));
            return new DateTime(year, month, day);
        }
    }
    catch
    {
        // Ignore parsing errors
    }

    return DateTime.MinValue;
}

// Request models for push-agent API (updated to remove ApiKey)
public class ShipmentPushRequest
{
    public string ClientId { get; set; } = string.Empty;
    public List<SageShipmentHeader> Shipments { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class DeliveryStatusUpdateRequest
{
    public string TripId { get; set; } = string.Empty;
    public string DeliveryId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // "pending" or "completed"
    public string? PodData { get; set; } // JSON string with POD details
    public string? DeviceId { get; set; }
    public string? DriverId { get; set; }
}
