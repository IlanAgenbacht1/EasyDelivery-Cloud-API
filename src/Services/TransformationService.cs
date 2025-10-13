using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using DeliveryCloudApi.Models;
using Newtonsoft.Json;
using System.Text.RegularExpressions;

namespace DeliveryCloudApi.Services
{
    public interface ITransformationService
    {
        Task<Dictionary<string, Trip>> ConvertShipmentsToTripsAsync(string clientId, List<SageShipmentHeader> shipments);
        Task<List<Delivery>> ConvertShipmentToDeliveriesAsync(SageShipmentHeader shipment);
    }

    public class TransformationService : ITransformationService
    {
        private readonly ILogger<TransformationService> _logger;
        private readonly IGeocodingService _geocodingService;

        public TransformationService(
            ILogger<TransformationService> logger,
            IGeocodingService geocodingService)
        {
            _logger = logger;
            _geocodingService = geocodingService;
        }

        public async Task<Dictionary<string, Trip>> ConvertShipmentsToTripsAsync(string clientId, List<SageShipmentHeader> shipments)
        {
            var trips = new Dictionary<string, Trip>();

            foreach (var shipment in shipments)
            {
                var orderNumber = shipment.ORDNUMBER.Trim();
                if (string.IsNullOrEmpty(orderNumber))
                    continue;

                // Determine trip ID: prioritize ROUTE field, fall back to order number
                var tripId = !string.IsNullOrEmpty(shipment.ROUTE) ? shipment.ROUTE.Trim() : orderNumber;

                _logger.LogDebug("Processing shipment {SHIUNIQ}: Order='{Order}', Route='{Route}' → TripId='{TripId}'", 
                    shipment.SHIUNIQ, orderNumber, shipment.ROUTE ?? "(null)", tripId);

                // Get or create trip for this batch of new shipments
                if (!trips.ContainsKey(tripId))
                {
                    trips[tripId] = new Trip
                    {
                        ClientId = clientId,
                        CreatedAt = DateTime.UtcNow,
                        Status = "available",
                        SessionInfo = new SessionInfo() // Empty initially
                    };
                }

                var trip = trips[tripId];

                // Create deliveries from shipment
                var deliveries = await ConvertShipmentToDeliveriesAsync(shipment);

                // Note: Deliveries will be saved separately as sub-collection
                // Here we just log for tracking
                _logger.LogDebug("Created {DeliveryCount} deliveries for trip {TripId} from shipment {SHIUNIQ}", 
                    deliveries.Count, tripId, shipment.SHIUNIQ);
            }

            // Log summary
            var routeBasedTrips = shipments.Count(s => !string.IsNullOrEmpty(s.ROUTE));
            var orderBasedTrips = shipments.Count(s => string.IsNullOrEmpty(s.ROUTE));
            
            _logger.LogInformation("Trip conversion completed: {TotalTrips} trips created from {TotalShipments} shipments. " +
                "Used ROUTE field: {RouteBasedCount}, Used Order Number: {OrderBasedCount}", 
                trips.Count, shipments.Count, routeBasedTrips, orderBasedTrips);

            return trips;
        }

        public async Task<List<Delivery>> ConvertShipmentToDeliveriesAsync(SageShipmentHeader shipment)
        {
            var deliveries = new List<Delivery>();

            // Build address string
            var addressParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(shipment.SHPADDR1)) addressParts.Add(shipment.SHPADDR1);
            if (!string.IsNullOrWhiteSpace(shipment.SHPADDR2)) addressParts.Add(shipment.SHPADDR2);
            if (!string.IsNullOrWhiteSpace(shipment.SHPADDR3)) addressParts.Add(shipment.SHPADDR3);
            if (!string.IsNullOrWhiteSpace(shipment.SHPCITY)) addressParts.Add(shipment.SHPCITY);
            if (!string.IsNullOrWhiteSpace(shipment.SHPSTATE)) addressParts.Add(shipment.SHPSTATE);
            if (!string.IsNullOrWhiteSpace(shipment.SHPZIP)) addressParts.Add(shipment.SHPZIP);

            var fullAddress = string.Join(", ", addressParts);

            // Clean customer name using SHPNAME → BILNAME fallback
            var originalCustomerName = !string.IsNullOrWhiteSpace(shipment.SHPNAME) ? shipment.SHPNAME : shipment.BILNAME;
            var cleanedCustomerName = CleanBusinessName(originalCustomerName);

            _logger.LogDebug("Customer name for {SHIUNIQ}: Source='{Source}' (using {Field}), Cleaned='{Cleaned}'", 
                shipment.SHIUNIQ, originalCustomerName, 
                !string.IsNullOrWhiteSpace(shipment.SHPNAME) ? "SHPNAME" : "BILNAME", 
                cleanedCustomerName);

            // Prepare geocoding query
            var geocodingQuery = string.IsNullOrWhiteSpace(cleanedCustomerName.Trim()) 
                ? fullAddress 
                : $"{cleanedCustomerName.Trim()}, {fullAddress}";

            // Get GPS coordinates
            var gpsLocationString = await _geocodingService.GetGpsLocationStringAsync(geocodingQuery);

            // Get contact details with fallback logic
            var contactName = !string.IsNullOrWhiteSpace(shipment.SHPCONTACT) ? shipment.SHPCONTACT : shipment.BILCONTACT;
            var contactNumber = !string.IsNullOrWhiteSpace(shipment.SHPPHONE) ? shipment.SHPPHONE : shipment.BILPHONE;
            
            // Use BILEMAIL for customer email
            var customerEmail = shipment.BILEMAIL ?? string.Empty;

            // Create delivery
            var delivery = new Delivery
            {
                Address = fullAddress,
                Customer = new Customer
                {
                    Name = cleanedCustomerName,
                    Contact = contactName,
                    Number = contactNumber,
                    Email = customerEmail
                },
                GpsLocation = gpsLocationString,
                OrderNumber = shipment.ORDNUMBER.Trim(),
                QuantityExpected = shipment.Details.Count.ToString(), // Total line items
                Status = "pending",
                // POD data initialized with empty template - mobile app will populate
                PodData = "{\"signatureUrl\": \"\", \"capturedGpsLocation\": \"[0° N, 0° E]\", \"photoUrl\": \"\", \"comment\": \"\", \"recipientName\": \"\", \"quantity\": \"\"}"
            };

            deliveries.Add(delivery);
            return deliveries;
        }

        /// <summary>
        /// Clean business names using the same logic as the original service
        /// </summary>
        private static string CleanBusinessName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

            try
            {
                var cleaned = name.Trim();

                // Step 1: Handle Trading As (HIGHEST PRIORITY)
                var tradingAsPattern = @"\s+(?:T/A|t/a|Ta|ta)\s+(.+)";
                var tradingAsMatch = Regex.Match(cleaned, tradingAsPattern);
                if (tradingAsMatch.Success)
                {
                    var tradingAsName = tradingAsMatch.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(tradingAsName))
                    {
                        return tradingAsName;
                    }
                }

                // Step 2: Remove Division patterns (if no Trading As found)
                var divisionPatterns = new[]
                {
                    @"\s+A\s+Div(?:ision)?\s+Of\s+.+",           // A Div Of / A Division Of
                    @"\s+A\s+DIV(?:ISION)?\s+OF\s+.+",           // A DIV OF / A DIVISION OF
                    @"\s+Div(?:ision)?\s+Of\s+.+",               // Division Of
                    @"\s+DIV(?:ISION)?\s+OF\s+.+",               // DIVISION OF
                    @"\s+(?:Div|DIV|Division|DIVISION)\s+.+",     // Div / Division + anything
                };

                foreach (var pattern in divisionPatterns)
                {
                    cleaned = Regex.Replace(cleaned, pattern, "", RegexOptions.IgnoreCase);
                }

                // Step 3: Remove content after legal entity suffixes
                var legalEntityPatterns = new[]
                {
                    @"(\(Pty\)\s*Ltd?)\s+.+",                    // (Pty) Ltd + anything
                    @"(\(PTY\)\s*LTD?)\s+.+",                    // (PTY) LTD + anything  
                    @"(Ltd?)\s+.+",                              // Ltd + anything
                    @"(LTD?)\s+.+",                              // LTD + anything
                    @"(Limited)\s+.+",                           // Limited + anything
                    @"(LIMITED)\s+.+"                            // LIMITED + anything
                };

                foreach (var pattern in legalEntityPatterns)
                {
                    var match = Regex.Match(cleaned, pattern);
                    if (match.Success)
                    {
                        // Keep everything up to and including the legal entity suffix
                        cleaned = cleaned.Substring(0, match.Groups[1].Index + match.Groups[1].Length).Trim();
                        break;
                    }
                }

                // Step 4: Clean up extra whitespace and normalize
                cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

                return cleaned;
            }
            catch (Exception)
            {
                // If anything goes wrong with regex processing, return the original trimmed name
                return name.Trim();
            }
        }
    }

    // Geocoding service interface and implementation
    public interface IGeocodingService
    {
        Task<GpsCoordinate> GetCoordinatesAsync(string address);
        Task<string> GetGpsLocationStringAsync(string address);
    }

    public class GeocodingService : IGeocodingService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<GeocodingService> _logger;
        private readonly IConfiguration _configuration;

        public GeocodingService(
            HttpClient httpClient, 
            ILogger<GeocodingService> logger,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _configuration = configuration;
        }

        public async Task<GpsCoordinate> GetCoordinatesAsync(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return new GpsCoordinate { Latitude = 0, Longitude = 0 };

            try
            {
                var apiKey = _configuration["GoogleMaps:ApiKey"];
                if (string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogWarning("Google Maps API key not configured");
                    return new GpsCoordinate { Latitude = 0, Longitude = 0 };
                }

                var encodedAddress = Uri.EscapeDataString(address);
                var url = $"https://maps.googleapis.com/maps/api/geocode/json?address={encodedAddress}&key={apiKey}";

                var response = await _httpClient.GetStringAsync(url);
                var geocodeResponse = JsonConvert.DeserializeObject<GeocodeResponse>(response);

                if (geocodeResponse?.Status == "OK" && geocodeResponse.Results?.Any() == true)
                {
                    var location = geocodeResponse.Results[0].Geometry.Location;
                    return new GpsCoordinate
                    {
                        Latitude = location.Lat,
                        Longitude = location.Lng
                    };
                }

                _logger.LogWarning("Geocoding failed for address: {Address}. Status: {Status}", 
                    address, geocodeResponse?.Status ?? "Unknown");
                return new GpsCoordinate { Latitude = 0, Longitude = 0 };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error geocoding address: {Address}", address);
                return new GpsCoordinate { Latitude = 0, Longitude = 0 };
            }
        }

        public async Task<string> GetGpsLocationStringAsync(string address)
        {
            var coordinate = await GetCoordinatesAsync(address);
            
            if (coordinate.Latitude == 0 && coordinate.Longitude == 0)
                return "[0° N, 0° E]";

            // Format like: "[34.090194° S, 18.843545° E]"
            var latDirection = coordinate.Latitude >= 0 ? "N" : "S";
            var lngDirection = coordinate.Longitude >= 0 ? "E" : "W";

            return $"[{Math.Abs(coordinate.Latitude):F6}° {latDirection}, {Math.Abs(coordinate.Longitude):F6}° {lngDirection}]";
        }
    }

    // Helper classes
    public class GpsCoordinate
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    // Google Maps API response models
    public class GeocodeResponse
    {
        public string Status { get; set; } = string.Empty;
        public List<GeocodeResult> Results { get; set; } = new();
    }

    public class GeocodeResult
    {
        public Geometry Geometry { get; set; } = new();
    }

    public class Geometry
    {
        public Location Location { get; set; } = new();
    }

    public class Location
    {
        public double Lat { get; set; }
        public double Lng { get; set; }
    }
}
