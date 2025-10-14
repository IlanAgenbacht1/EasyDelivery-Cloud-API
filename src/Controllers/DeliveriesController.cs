using Microsoft.AspNetCore.Mvc;
using DeliveryCloudApi.Services;
using DeliveryCloudApi.Models;
using Newtonsoft.Json;

namespace DeliveryCloudApi.Controllers
{
    [ApiController]
    [Route("api/trips/{tripId}/[controller]")]
    [Produces("application/json")]
    public class DeliveriesController : ControllerBase
    {
        private readonly IFirestoreService _firestoreService;
        private readonly ILogger<DeliveriesController> _logger;
        private readonly IPodPdfService _podPdfService;
        private readonly IEmailService _emailService;

        public DeliveriesController(IFirestoreService firestoreService, 
            ILogger<DeliveriesController> logger, 
            IPodPdfService podPdfService, 
            IEmailService emailService)
        {
            _firestoreService = firestoreService;
            _logger = logger;
            _podPdfService = podPdfService;
            _emailService = emailService;
        }

        /// <summary>
        /// List deliveries for a specific trip
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetTripDeliveries(string tripId, [FromQuery] string sessionId, [FromQuery] string? status = null)
        {
            try
            {
                if (string.IsNullOrEmpty(tripId) || string.IsNullOrEmpty(sessionId))
                {
                    return BadRequest(new { error = "TripId and SessionId are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(sessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Validate trip and ownership
                var validationResult = await ValidateTripAccess(tripId, session.DriverId);
                if (validationResult != null) return validationResult;

                // Get deliveries
                var deliveries = await _firestoreService.GetTripDeliveriesAsync(tripId, status);
                var deliveryList = deliveries.Select(d => new
                {
                    deliveryId = d.Key,
                    address = d.Value.Address,
                    customer = new
                    {
                        name = d.Value.Customer.Name,
                        contact = d.Value.Customer.Contact,
                        number = d.Value.Customer.Number,
                        email = d.Value.Customer.Email
                    },
                    orderNumber = d.Value.OrderNumber,
                    quantityExpected = d.Value.QuantityExpected,
                    status = d.Value.Status,
                    gpsLocation = d.Value.GpsLocation,
                    podData = ParsePodData(d.Value.PodData)
                });

                // Update session last active
                await _firestoreService.UpdateSessionLastActiveAsync(sessionId, DateTime.UtcNow);

                return Ok(new
                {
                    tripId = tripId,
                    driverId = session.DriverId,
                    status = status,
                    deliveries = deliveryList,
                    count = deliveryList.Count()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting deliveries for trip {TripId}", tripId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get specific delivery details
        /// </summary>
        [HttpGet("{deliveryId}")]
        public async Task<IActionResult> GetDelivery(string tripId, string deliveryId, [FromQuery] string sessionId)
        {
            try
            {
                if (string.IsNullOrEmpty(tripId) || string.IsNullOrEmpty(deliveryId) || string.IsNullOrEmpty(sessionId))
                {
                    return BadRequest(new { error = "TripId, DeliveryId, and SessionId are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(sessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Validate trip and ownership
                var validationResult = await ValidateTripAccess(tripId, session.DriverId);
                if (validationResult != null) return validationResult;

                // Get delivery
                var delivery = await _firestoreService.GetDeliveryAsync(tripId, deliveryId);
                if (delivery == null)
                {
                    return NotFound(new { error = "Delivery not found" });
                }

                // Update session last active
                await _firestoreService.UpdateSessionLastActiveAsync(sessionId, DateTime.UtcNow);

                return Ok(new
                {
                    tripId = tripId,
                    deliveryId = deliveryId,
                    address = delivery.Address,
                    customer = new
                    {
                        name = delivery.Customer.Name,
                        contact = delivery.Customer.Contact,
                        number = delivery.Customer.Number,
                        email = delivery.Customer.Email
                    },
                    orderNumber = delivery.OrderNumber,
                    quantityExpected = delivery.QuantityExpected,
                    status = delivery.Status,
                    gpsLocation = delivery.GpsLocation,
                    podData = ParsePodData(delivery.PodData)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting delivery {DeliveryId} for trip {TripId}", deliveryId, tripId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Helper method to validate trip access for the driver
        /// </summary>
        private async Task<IActionResult?> ValidateTripAccess(string tripId, string driverId)
        {
            var trip = await _firestoreService.GetTripAsync(tripId);
            if (trip == null)
            {
                return NotFound(new { error = "Trip not found" });
            }

            // Allow access if trip is claimed/in_progress by this driver, or if it's completed by this driver
            if (trip.Status == "available" || 
                (trip.SessionInfo.DriverId != driverId && !string.IsNullOrEmpty(trip.SessionInfo.DriverId)))
            {
                return BadRequest(new { error = "Trip is not accessible by this driver" });
            }

            return null;
        }

        /// <summary>
        /// Helper method to parse POD data JSON
        /// </summary>
        private object ParsePodData(string podDataJson)
        {
            try
            {
                if (string.IsNullOrEmpty(podDataJson))
                {
                    return new
                    {
                        signatureUrl = "",
                        capturedGpsLocation = "[0° N, 0° E]",
                        photoUrl = "",
                        comment = "",
                        recipientName = "",
                        quantity = ""
                    };
                }

                return JsonConvert.DeserializeObject(podDataJson) ?? new { };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing POD data: {PodData}", podDataJson);
                return new { error = "Invalid POD data format" };
            }
        }

        /// <summary>
        /// Update delivery status
        /// </summary>
        [HttpPost("{deliveryId}/update")]
        public async Task<IActionResult> UpdateDeliveryStatus(string tripId, string deliveryId, [FromBody] UpdateDeliveryStatusRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(tripId) || string.IsNullOrEmpty(deliveryId) || string.IsNullOrEmpty(request.SessionId))
                {
                    return BadRequest(new { error = "TripId, DeliveryId, and SessionId are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(request.SessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Validate trip and ownership
                var validationResult = await ValidateTripAccess(tripId, session.DriverId);
                if (validationResult != null) return validationResult;

                // Validate status value
                if (request.Status != "pending" && request.Status != "completed")
                {
                    return BadRequest(new { error = "Status must be 'pending' or 'completed'", providedStatus = request.Status });
                }

                // Check if delivery exists
                var delivery = await _firestoreService.GetDeliveryAsync(tripId, deliveryId);
                if (delivery == null)
                {
                    return NotFound(new { error = "Delivery not found" });
                }

                // Update delivery status
                await _firestoreService.UpdateDeliveryStatusAsync(tripId, deliveryId, request.Status, request.PodData);
                
                // Update session last active
                await _firestoreService.UpdateSessionLastActiveAsync(request.SessionId, DateTime.UtcNow);

                _logger.LogInformation("Delivery {DeliveryId} status updated to {Status} by driver {DriverId}", 
                    deliveryId, request.Status, session.DriverId);

                return Ok(new
                {
                    success = true,
                    message = "Delivery status updated successfully",
                    tripId = tripId,
                    deliveryId = deliveryId,
                    previousStatus = delivery.Status,
                    newStatus = request.Status,
                    podDataUpdated = !string.IsNullOrWhiteSpace(request.PodData),
                    updatedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating delivery {DeliveryId} status for trip {TripId}", deliveryId, tripId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Complete delivery with POD data
        /// </summary>
        [HttpPost("{deliveryId}/complete")]
        public async Task<IActionResult> CompleteDelivery(string tripId, string deliveryId, [FromBody] CompleteDeliveryRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(tripId) || string.IsNullOrEmpty(deliveryId) || string.IsNullOrEmpty(request.SessionId))
                {
                    return BadRequest(new { error = "TripId, DeliveryId, and SessionId are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(request.SessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Validate trip and ownership
                var validationResult = await ValidateTripAccess(tripId, session.DriverId);
                if (validationResult != null) return validationResult;

                // Check if delivery exists
                var delivery = await _firestoreService.GetDeliveryAsync(tripId, deliveryId);
                if (delivery == null)
                {
                    return NotFound(new { error = "Delivery not found" });
                }

                // Prevent duplicate completion for deliveries (trips can be reactivated for short shipments)
                if (delivery.Status == "completed")
                {
                    return BadRequest(new { error = "Delivery is already completed" });
                }

                // Prepare POD data
                var podData = new
                {
                    signatureUrl = request.SignatureUrl ?? "",
                    capturedGpsLocation = request.CapturedGpsLocation ?? "[0° N, 0° E]",
                    photoUrl = request.PhotoUrl ?? "",
                    comment = request.Comment ?? "",
                    recipientName = request.RecipientName ?? "",
                    quantity = request.Quantity ?? "",
                    completedAt = DateTime.UtcNow,
                    completedBy = session.DriverId
                };

                var podDataJson = JsonConvert.SerializeObject(podData);

                // Complete delivery with POD data
                await _firestoreService.UpdateDeliveryStatusAsync(tripId, deliveryId, "completed", podDataJson);
                
                // Update session last active
                await _firestoreService.UpdateSessionLastActiveAsync(request.SessionId, DateTime.UtcNow);

                _logger.LogInformation("Delivery {DeliveryId} completed with POD by driver {DriverId}", 
                    deliveryId, session.DriverId);
                
                // Trigger automatic POD PDF generation and email sending (fire and forget)
                _ = Task.Run(async () => await ProcessDeliveryCompletionAsync(tripId, deliveryId, session.ClientId));

                return Ok(new
                {
                    success = true,
                    message = "Delivery completed successfully with POD data",
                    tripId = tripId,
                    deliveryId = deliveryId,
                    previousStatus = delivery.Status,
                    newStatus = "completed",
                    podData = podData,
                    completedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error completing delivery {DeliveryId} for trip {TripId}", deliveryId, tripId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Batch update delivery statuses
        /// </summary>
        [HttpPost("batch-update")]
        public async Task<IActionResult> BatchUpdateDeliveries(string tripId, [FromBody] BatchUpdateDeliveriesRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(tripId) || string.IsNullOrEmpty(request.SessionId) || !request.Updates.Any())
                {
                    return BadRequest(new { error = "TripId, SessionId, and Updates are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(request.SessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Validate trip and ownership
                var validationResult = await ValidateTripAccess(tripId, session.DriverId);
                if (validationResult != null) return validationResult;

                // Validate all updates
                var validationErrors = new List<string>();
                foreach (var update in request.Updates)
                {
                    if (string.IsNullOrEmpty(update.DeliveryId))
                    {
                        validationErrors.Add("DeliveryId is required for all updates");
                        continue;
                    }
                    if (update.Status != "pending" && update.Status != "completed")
                    {
                        validationErrors.Add($"Invalid status '{update.Status}' for delivery {update.DeliveryId}");
                    }
                }

                if (validationErrors.Any())
                {
                    return BadRequest(new { error = "Validation failed", details = validationErrors });
                }

                // Process batch updates
                var results = new List<object>();
                var successCount = 0;
                var failureCount = 0;

                foreach (var update in request.Updates)
                {
                    try
                    {
                        // Check if delivery exists
                        var delivery = await _firestoreService.GetDeliveryAsync(tripId, update.DeliveryId);
                        if (delivery == null)
                        {
                            results.Add(new
                            {
                                deliveryId = update.DeliveryId,
                                success = false,
                                error = "Delivery not found"
                            });
                            failureCount++;
                            continue;
                        }

                        // Update delivery
                        await _firestoreService.UpdateDeliveryStatusAsync(tripId, update.DeliveryId, update.Status, update.PodData);
                        
                        results.Add(new
                        {
                            deliveryId = update.DeliveryId,
                            success = true,
                            previousStatus = delivery.Status,
                            newStatus = update.Status
                        });
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error updating delivery {DeliveryId} in batch operation", update.DeliveryId);
                        results.Add(new
                        {
                            deliveryId = update.DeliveryId,
                            success = false,
                            error = "Update failed"
                        });
                        failureCount++;
                    }
                }

                // Update session last active
                await _firestoreService.UpdateSessionLastActiveAsync(request.SessionId, DateTime.UtcNow);

                _logger.LogInformation("Batch update completed for trip {TripId}: {SuccessCount} successful, {FailureCount} failed", 
                    tripId, successCount, failureCount);

                return Ok(new
                {
                    success = true,
                    message = "Batch update completed",
                    tripId = tripId,
                    totalUpdates = request.Updates.Count,
                    successfulUpdates = successCount,
                    failedUpdates = failureCount,
                    results = results,
                    updatedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in batch update for trip {TripId}", tripId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Batch complete deliveries with POD data
        /// </summary>
        [HttpPost("batch-complete")]
        public async Task<IActionResult> BatchCompleteDeliveries(string tripId, [FromBody] BatchCompleteDeliveriesRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(tripId) || string.IsNullOrEmpty(request.SessionId) || !request.Deliveries.Any())
                {
                    return BadRequest(new { error = "TripId, SessionId, and Deliveries are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(request.SessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Validate trip and ownership
                var validationResult = await ValidateTripAccess(tripId, session.DriverId);
                if (validationResult != null) return validationResult;

                // Process batch completions
                var results = new List<object>();
                var successCount = 0;
                var failureCount = 0;

                foreach (var completion in request.Deliveries)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(completion.DeliveryId))
                        {
                            results.Add(new
                            {
                                deliveryId = completion.DeliveryId,
                                success = false,
                                error = "DeliveryId is required"
                            });
                            failureCount++;
                            continue;
                        }

                        // Check if delivery exists
                        var delivery = await _firestoreService.GetDeliveryAsync(tripId, completion.DeliveryId);
                        if (delivery == null)
                        {
                            results.Add(new
                            {
                                deliveryId = completion.DeliveryId,
                                success = false,
                                error = "Delivery not found"
                            });
                            failureCount++;
                            continue;
                        }

                        if (delivery.Status == "completed")
                        {
                            results.Add(new
                            {
                                deliveryId = completion.DeliveryId,
                                success = false,
                                error = "Delivery is already completed"
                            });
                            failureCount++;
                            continue;
                        }

                        // Prepare POD data
                        var podData = new
                        {
                            signatureUrl = completion.SignatureUrl ?? "",
                            capturedGpsLocation = completion.CapturedGpsLocation ?? "[0° N, 0° E]",
                            photoUrl = completion.PhotoUrl ?? "",
                            comment = completion.Comment ?? "",
                            recipientName = completion.RecipientName ?? "",
                            quantity = completion.Quantity ?? "",
                            completedAt = DateTime.UtcNow,
                            completedBy = session.DriverId
                        };

                        var podDataJson = JsonConvert.SerializeObject(podData);

                        // Complete delivery
                        await _firestoreService.UpdateDeliveryStatusAsync(tripId, completion.DeliveryId, "completed", podDataJson);
                        
                        // Trigger automatic POD PDF generation and email sending (fire and forget)
                        _ = Task.Run(async () => await ProcessDeliveryCompletionAsync(tripId, completion.DeliveryId, session.ClientId));
                        
                        results.Add(new
                        {
                            deliveryId = completion.DeliveryId,
                            success = true,
                            previousStatus = delivery.Status,
                            newStatus = "completed",
                            podData = podData
                        });
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error completing delivery {DeliveryId} in batch operation", completion.DeliveryId);
                        results.Add(new
                        {
                            deliveryId = completion.DeliveryId,
                            success = false,
                            error = "Completion failed"
                        });
                        failureCount++;
                    }
                }

                // Update session last active
                await _firestoreService.UpdateSessionLastActiveAsync(request.SessionId, DateTime.UtcNow);

                _logger.LogInformation("Batch completion finished for trip {TripId}: {SuccessCount} successful, {FailureCount} failed", 
                    tripId, successCount, failureCount);

                return Ok(new
                {
                    success = true,
                    message = "Batch completion finished",
                    tripId = tripId,
                    totalDeliveries = request.Deliveries.Count,
                    successfulCompletions = successCount,
                    failedCompletions = failureCount,
                    results = results,
                    completedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in batch completion for trip {TripId}", tripId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Complete delivery offline - for WorkManager background sync
        /// Uses device + driver authentication instead of session validation
        /// </summary>
        [HttpPost("{deliveryId}/complete-offline")]
        public async Task<IActionResult> CompleteDeliveryOffline(string tripId, string deliveryId, [FromBody] CompleteDeliveryOfflineRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(tripId) || 
                    string.IsNullOrEmpty(deliveryId) || 
                    string.IsNullOrEmpty(request.DriverId) || 
                    string.IsNullOrEmpty(request.DeviceId))
                {
                    return BadRequest(new { error = "TripId, DeliveryId, DriverId, and DeviceId are required" });
                }

                // Validate device exists and is approved
                var device = await _firestoreService.GetDeviceAsync(request.DeviceId);
                if (device == null || !device.IsApproved)
                {
                    return BadRequest(new { error = "Invalid or unapproved device" });
                }

                // Validate driver exists and is active
                var driver = await _firestoreService.GetDriverAsync(request.DriverId);
                if (driver == null || !driver.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive driver" });
                }

                // Verify driver and device belong to same client
                if (driver.ClientId != device.ClientId)
                {
                    return BadRequest(new { error = "Driver and device client mismatch" });
                }

                // Validate trip exists and was assigned to this driver
                var trip = await _firestoreService.GetTripAsync(tripId);
                if (trip == null)
                {
                    return NotFound(new { error = "Trip not found" });
                }

                // Check if trip was ever assigned to this driver (allow completed trips for offline sync)
                if (trip.SessionInfo.DriverId != request.DriverId)
                {
                    return BadRequest(new { error = "Trip was not assigned to this driver" });
                }

                // Verify trip belongs to same client
                if (trip.ClientId != driver.ClientId)
                {
                    return BadRequest(new { error = "Trip does not belong to driver's client" });
                }

                // Check if delivery exists
                var delivery = await _firestoreService.GetDeliveryAsync(tripId, deliveryId);
                if (delivery == null)
                {
                    return NotFound(new { error = "Delivery not found" });
                }

                // Allow re-completion for offline sync scenarios (don't block if already completed)
                // This handles cases where the mobile app completed offline but sync failed
                
                // Prepare POD data with offline completion info
                var podData = new
                {
                    signatureUrl = request.SignatureUrl ?? "",
                    capturedGpsLocation = request.CapturedGpsLocation ?? "[0° N, 0° E]",
                    photoUrl = request.PhotoUrl ?? "",
                    comment = request.Comment ?? "",
                    recipientName = request.RecipientName ?? "",
                    quantity = request.Quantity ?? "",
                    completedAt = request.CompletedAt ?? DateTime.UtcNow,
                    completedBy = request.DriverId,
                    offlineSync = true,
                    syncedAt = DateTime.UtcNow
                };

                var podDataJson = JsonConvert.SerializeObject(podData);

                // Complete/update delivery with offline POD data
                await _firestoreService.UpdateDeliveryStatusAsync(tripId, deliveryId, "completed", podDataJson);
                
                // Update device last seen
                await _firestoreService.UpdateDeviceLastSeenAsync(request.DeviceId, DateTime.UtcNow);

                _logger.LogInformation("Delivery {DeliveryId} completed offline and synced by driver {DriverId} from device {DeviceId}", 
                    deliveryId, request.DriverId, request.DeviceId);
                    
                // Trigger automatic POD PDF generation and email sending (fire and forget)
                _ = Task.Run(async () => await ProcessDeliveryCompletionAsync(tripId, deliveryId, driver.ClientId));

                return Ok(new
                {
                    success = true,
                    message = "Delivery completed successfully via offline sync",
                    tripId = tripId,
                    deliveryId = deliveryId,
                    previousStatus = delivery.Status,
                    newStatus = "completed",
                    podData = podData,
                    syncedAt = DateTime.UtcNow,
                    offlineCompletion = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error completing delivery {DeliveryId} offline for trip {TripId}", deliveryId, tripId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Complete POD with file uploads - all-in-one endpoint
        /// </summary>
        [HttpPost("{deliveryId}/pod")]
        public async Task<IActionResult> CompletePodWithFiles(string tripId, string deliveryId, [FromForm] CompletePodWithFilesRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(tripId) || 
                    string.IsNullOrEmpty(deliveryId) || 
                    string.IsNullOrEmpty(request.SessionId))
                {
                    return BadRequest(new { error = "TripId, DeliveryId, and SessionId are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(request.SessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Validate trip and ownership
                var validationResult = await ValidateTripAccess(tripId, session.DriverId);
                if (validationResult != null) return validationResult;

                // Check if delivery exists
                var delivery = await _firestoreService.GetDeliveryAsync(tripId, deliveryId);
                if (delivery == null)
                {
                    return NotFound(new { error = "Delivery not found" });
                }

                // Prevent duplicate completion for deliveries (trips can be reactivated for short shipments)
                if (delivery.Status == "completed")
                {
                    return BadRequest(new { error = "Delivery is already completed" });
                }

                var uploadedFiles = new List<object>();
                var signatureUrl = "";
                var photoUrl = "";

                // Upload signature file if provided
                if (request.SignatureFile != null)
                {
                    try
                    {
                        var signatureResult = await UploadFileForPod(request.SignatureFile, "signature", 
                            session, tripId, deliveryId);
                        if (signatureResult != null)
                        {
                            signatureUrl = signatureResult.PublicUrl;
                            uploadedFiles.Add(new 
                            {
                                fileType = "signature",
                                fileId = signatureResult.FileId,
                                fileName = signatureResult.FileName,
                                fileSize = signatureResult.FileSize,
                                uploadedAt = signatureResult.UploadedAt,
                                publicUrl = signatureResult.PublicUrl
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error uploading signature for delivery {DeliveryId}", deliveryId);
                        return BadRequest(new { error = "Failed to upload signature file" });
                    }
                }

                // Upload photo file if provided
                if (request.PhotoFile != null)
                {
                    try
                    {
                        var photoResult = await UploadFileForPod(request.PhotoFile, "photo", 
                            session, tripId, deliveryId);
                        if (photoResult != null)
                        {
                            photoUrl = photoResult.PublicUrl;
                            uploadedFiles.Add(new 
                            {
                                fileType = "photo",
                                fileId = photoResult.FileId,
                                fileName = photoResult.FileName,
                                fileSize = photoResult.FileSize,
                                uploadedAt = photoResult.UploadedAt,
                                publicUrl = photoResult.PublicUrl
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error uploading photo for delivery {DeliveryId}", deliveryId);
                        return BadRequest(new { error = "Failed to upload photo file" });
                    }
                }

                // Prepare comprehensive POD data
                var podData = new
                {
                    signatureUrl = signatureUrl,
                    capturedGpsLocation = request.CapturedGpsLocation ?? "[0° N, 0° E]",
                    photoUrl = photoUrl,
                    comment = request.Comment ?? "",
                    recipientName = request.RecipientName ?? "",
                    quantity = request.Quantity ?? "",
                    completedAt = DateTime.UtcNow,
                    completedBy = session.DriverId,
                    uploadedFiles = uploadedFiles,
                    podComplete = true
                };

                var podDataJson = JsonConvert.SerializeObject(podData);

                // Complete delivery with comprehensive POD data
                await _firestoreService.UpdateDeliveryStatusAsync(tripId, deliveryId, "completed", podDataJson);
                
                // Update session last active
                await _firestoreService.UpdateSessionLastActiveAsync(request.SessionId, DateTime.UtcNow);

                _logger.LogInformation("Delivery {DeliveryId} completed with POD and {FileCount} files by driver {DriverId}", 
                    deliveryId, uploadedFiles.Count, session.DriverId);
                    
                // Trigger automatic POD PDF generation and email sending (fire and forget)
                _ = Task.Run(async () => await ProcessDeliveryCompletionAsync(tripId, deliveryId, session.ClientId));

                return Ok(new
                {
                    success = true,
                    message = "Delivery completed successfully with POD and file uploads",
                    tripId = tripId,
                    deliveryId = deliveryId,
                    previousStatus = delivery.Status,
                    newStatus = "completed",
                    podData = podData,
                    filesUploaded = uploadedFiles.Count,
                    uploadedFiles = uploadedFiles,
                    completedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error completing POD with files for delivery {DeliveryId}", deliveryId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Helper method to upload files for POD
        /// </summary>
        private async Task<FileUploadResult?> UploadFileForPod(IFormFile file, string fileType, 
            Session session, string tripId, string deliveryId)
        {
            // File validation
            if (file.Length == 0 || file.Length > 10 * 1024 * 1024) // 10MB limit
            {
                throw new ArgumentException($"Invalid file size for {fileType}");
            }

            var allowedImageTypes = new[] { "image/jpeg", "image/jpg", "image/png", "image/webp" };
            if (!allowedImageTypes.Contains(file.ContentType.ToLower()))
            {
                throw new ArgumentException($"Invalid file type for {fileType}. Must be an image.");
            }

            // Generate unique file ID and name
            var fileId = Guid.NewGuid().ToString();
            var fileExtension = GetFileExtension(file.ContentType);
            var fileName = $"{fileType}_{fileId}.{fileExtension}";
            
            // Create upload directory structure: uploads/{clientId}/{fileType}s/
            var basePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
            var uploadPath = Path.Combine(basePath, session.ClientId, $"{fileType}s");
            Directory.CreateDirectory(uploadPath);
            
            // Save file
            var filePath = Path.Combine(uploadPath, fileName);
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            // Create file metadata
            var fileMetadata = new FileMetadata
            {
                FileName = fileName,
                OriginalFileName = file.FileName,
                ContentType = file.ContentType,
                FileSize = file.Length,
                FileType = fileType,
                UploadedBy = session.DriverId,
                UploadedFrom = session.DeviceId,
                ClientId = session.ClientId,
                TripId = tripId,
                DeliveryId = deliveryId,
                UploadedAt = DateTime.UtcNow,
                FilePath = filePath,
                PublicUrl = $"/api/files/{fileId}"
            };

            // Save metadata to Firestore
            await _firestoreService.SaveFileMetadataAsync(fileId, fileMetadata);

            return new FileUploadResult
            {
                FileId = fileId,
                FileName = fileName,
                FileSize = file.Length,
                UploadedAt = fileMetadata.UploadedAt,
                PublicUrl = fileMetadata.PublicUrl
            };
        }

        /// <summary>
        /// Helper method to get file extension from content type
        /// </summary>
        private string GetFileExtension(string contentType)
        {
            return contentType.ToLower() switch
            {
                "image/jpeg" => "jpg",
                "image/jpg" => "jpg",
                "image/png" => "png",
                "image/webp" => "webp",
                _ => "bin"
            };
        }
        
        /// <summary>
        /// Automatically processes POD generation and email sending after delivery completion
        /// This runs asynchronously to avoid blocking the completion response
        /// </summary>
        private async Task ProcessDeliveryCompletionAsync(string tripId, string deliveryId, string clientId)
        {
            try
            {
                _logger.LogInformation("Starting automatic POD processing for delivery {DeliveryId} in trip {TripId}", 
                    deliveryId, tripId);
                
                // Generate PDF POD document
                var podDocumentId = await _podPdfService.GeneratePodPdfAsync(tripId, deliveryId);
                if (string.IsNullOrEmpty(podDocumentId))
                {
                    _logger.LogError("Failed to generate PDF for delivery {DeliveryId}", deliveryId);
                    return;
                }
                
                _logger.LogDebug("Generated PDF POD document {PodDocumentId} for delivery {DeliveryId}", 
                    podDocumentId, deliveryId);
                
                // Send email notifications asynchronously
                await _emailService.SendPodEmailsAsync(tripId, deliveryId, "system");
                
                _logger.LogInformation("Completed automatic POD processing for delivery {DeliveryId}", deliveryId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in automatic POD processing for delivery {DeliveryId} in trip {TripId}", 
                    deliveryId, tripId);
                // Don't rethrow - this is a background operation that shouldn't affect the completion response
            }
        }
    }

    // Helper classes for POD with files
    public class FileUploadResult
    {
        public string FileId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime UploadedAt { get; set; }
        public string PublicUrl { get; set; } = string.Empty;
    }

    // Request models for delivery operations
    public class UpdateDeliveryStatusRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // "pending" or "completed"
        public string? PodData { get; set; } // JSON string with POD details
    }

    public class CompleteDeliveryRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public string? SignatureUrl { get; set; } // Firebase Storage URL
        public string? CapturedGpsLocation { get; set; }
        public string? PhotoUrl { get; set; } // Firebase Storage URL
        public string? Comment { get; set; }
        public string? RecipientName { get; set; }
        public string? Quantity { get; set; }
    }

    // Batch operation models
    public class BatchUpdateDeliveriesRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public List<DeliveryStatusUpdate> Updates { get; set; } = new();
    }

    public class DeliveryStatusUpdate
    {
        public string DeliveryId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // "pending" or "completed"
        public string? PodData { get; set; }
    }

    public class BatchCompleteDeliveriesRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public List<DeliveryCompletion> Deliveries { get; set; } = new();
    }

    public class DeliveryCompletion
    {
        public string DeliveryId { get; set; } = string.Empty;
        public string? SignatureUrl { get; set; }
        public string? CapturedGpsLocation { get; set; }
        public string? PhotoUrl { get; set; }
        public string? Comment { get; set; }
        public string? RecipientName { get; set; }
        public string? Quantity { get; set; }
    }

    // Offline completion model
    public class CompleteDeliveryOfflineRequest
    {
        public string DriverId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string? SignatureUrl { get; set; }
        public string? CapturedGpsLocation { get; set; }
        public string? PhotoUrl { get; set; }
        public string? Comment { get; set; }
        public string? RecipientName { get; set; }
        public string? Quantity { get; set; }
        public DateTime? CompletedAt { get; set; } // When it was actually completed offline
    }

    // POD with files model
    public class CompletePodWithFilesRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public IFormFile? SignatureFile { get; set; }
        public IFormFile? PhotoFile { get; set; }
        public string? CapturedGpsLocation { get; set; }
        public string? Comment { get; set; }
        public string? RecipientName { get; set; }
        public string? Quantity { get; set; }
    }
}
