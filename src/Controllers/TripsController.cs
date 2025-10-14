using Microsoft.AspNetCore.Mvc;
using DeliveryCloudApi.Services;
using DeliveryCloudApi.Models;

namespace DeliveryCloudApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class TripsController : ControllerBase
    {
        private readonly IFirestoreService _firestoreService;
        private readonly ILogger<TripsController> _logger;

        public TripsController(IFirestoreService firestoreService, ILogger<TripsController> logger)
        {
            _firestoreService = firestoreService;
            _logger = logger;
        }

        /// <summary>
        /// List available trips filtered by client
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAvailableTrips([FromQuery] string clientId, [FromQuery] string? status = "available")
        {
            try
            {
                if (string.IsNullOrEmpty(clientId))
                {
                    return BadRequest(new { error = "ClientId is required" });
                }

                var trips = await _firestoreService.GetTripsByClientAndStatusAsync(clientId, status ?? "available");
                var tripList = trips.Select(t => new
                {
                    tripId = t.Key,
                    clientId = t.Value.ClientId,
                    status = t.Value.Status,
                    createdAt = t.Value.CreatedAt,
                    claimedAt = t.Value.ClaimedAt,
                    completedAt = t.Value.CompletedAt,
                    sessionInfo = t.Value.SessionInfo
                });

                return Ok(new
                {
                    clientId = clientId,
                    status = status,
                    trips = tripList,
                    count = tripList.Count()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting available trips for client {ClientId}", clientId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get driver's claimed trips
        /// </summary>
        [HttpGet("my")]
        public async Task<IActionResult> GetMyTrips([FromQuery] string sessionId)
        {
            try
            {
                if (string.IsNullOrEmpty(sessionId))
                {
                    return BadRequest(new { error = "SessionId is required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(sessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                var trips = await _firestoreService.GetDriverTripsAsync(session.DriverId);
                var tripList = trips.Select(t => new
                {
                    tripId = t.Key,
                    clientId = t.Value.ClientId,
                    status = t.Value.Status,
                    createdAt = t.Value.CreatedAt,
                    claimedAt = t.Value.ClaimedAt,
                    completedAt = t.Value.CompletedAt,
                    sessionInfo = t.Value.SessionInfo
                });

                // Update session last active
                await _firestoreService.UpdateSessionLastActiveAsync(sessionId, DateTime.UtcNow);

                return Ok(new
                {
                    driverId = session.DriverId,
                    clientId = session.ClientId,
                    trips = tripList,
                    count = tripList.Count()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting trips for session {SessionId}", sessionId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get specific trip details
        /// </summary>
        [HttpGet("{tripId}")]
        public async Task<IActionResult> GetTrip(string tripId, [FromQuery] string? sessionId = null)
        {
            try
            {
                if (string.IsNullOrEmpty(tripId))
                {
                    return BadRequest(new { error = "TripId is required" });
                }

                var trip = await _firestoreService.GetTripAsync(tripId);
                if (trip == null)
                {
                    return NotFound(new { error = "Trip not found" });
                }

                // If sessionId is provided, validate it and update last active
                if (!string.IsNullOrEmpty(sessionId))
                {
                    var session = await _firestoreService.GetSessionAsync(sessionId);
                    if (session != null && session.IsActive)
                    {
                        await _firestoreService.UpdateSessionLastActiveAsync(sessionId, DateTime.UtcNow);
                    }
                }

                return Ok(new
                {
                    tripId = tripId,
                    clientId = trip.ClientId,
                    status = trip.Status,
                    createdAt = trip.CreatedAt,
                    claimedAt = trip.ClaimedAt,
                    completedAt = trip.CompletedAt,
                    sessionInfo = trip.SessionInfo
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting trip {TripId}", tripId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Claim trip with session info
        /// </summary>
        [HttpPost("{tripId}/claim")]
        public async Task<IActionResult> ClaimTrip(string tripId, [FromBody] ClaimTripRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(tripId) || string.IsNullOrEmpty(request.SessionId))
                {
                    return BadRequest(new { error = "TripId and SessionId are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(request.SessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Validate trip exists and is available
                var trip = await _firestoreService.GetTripAsync(tripId);
                if (trip == null)
                {
                    return NotFound(new { error = "Trip not found" });
                }

                if (trip.Status != "available")
                {
                    return BadRequest(new { error = $"Trip is not available for claiming. Current status: {trip.Status}" });
                }

                // Verify trip belongs to same client as session
                if (trip.ClientId != session.ClientId)
                {
                    return BadRequest(new { error = "Trip does not belong to your client" });
                }

                // Check if driver already has claimed trips
                var driverTrips = await _firestoreService.GetDriverTripsAsync(session.DriverId);
                var activeTripCount = driverTrips.Count(t => t.Value.Status == "claimed" || t.Value.Status == "in_progress");
                
                if (activeTripCount >= 1) // Limit to one active trip per driver
                {
                    return BadRequest(new { error = "Driver already has an active trip. Complete or release current trip before claiming another." });
                }

                // Claim the trip
                await _firestoreService.ClaimTripAsync(tripId, session.DriverId, session.DeviceId, session.VehicleId, DateTime.UtcNow);
                
                // Update session last active
                await _firestoreService.UpdateSessionLastActiveAsync(request.SessionId, DateTime.UtcNow);

                _logger.LogInformation("Trip {TripId} claimed by driver {DriverId}", tripId, session.DriverId);

                return Ok(new
                {
                    success = true,
                    message = "Trip claimed successfully",
                    tripId = tripId,
                    driverId = session.DriverId,
                    vehicleId = session.VehicleId,
                    claimedAt = DateTime.UtcNow,
                    status = "claimed"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error claiming trip {TripId}", tripId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Start trip (claimed → in_progress)
        /// </summary>
        [HttpPost("{tripId}/start")]
        public async Task<IActionResult> StartTrip(string tripId, [FromBody] TripActionRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(tripId) || string.IsNullOrEmpty(request.SessionId))
                {
                    return BadRequest(new { error = "TripId and SessionId are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(request.SessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Validate trip and ownership
                var validationResult = await ValidateTripOwnership(tripId, session.DriverId);
                if (validationResult != null) return validationResult;

                var trip = await _firestoreService.GetTripAsync(tripId);
                if (trip!.Status != "claimed")
                {
                    return BadRequest(new { error = $"Trip cannot be started from current status: {trip.Status}" });
                }

                // Start the trip
                await _firestoreService.UpdateTripStatusAsync(tripId, "in_progress", null, null);
                
                // Update session last active
                await _firestoreService.UpdateSessionLastActiveAsync(request.SessionId, DateTime.UtcNow);

                _logger.LogInformation("Trip {TripId} started by driver {DriverId}", tripId, session.DriverId);

                return Ok(new
                {
                    success = true,
                    message = "Trip started successfully",
                    tripId = tripId,
                    driverId = session.DriverId,
                    status = "in_progress",
                    startedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting trip {TripId}", tripId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Complete trip
        /// </summary>
        [HttpPost("{tripId}/complete")]
        public async Task<IActionResult> CompleteTrip(string tripId, [FromBody] TripActionRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(tripId) || string.IsNullOrEmpty(request.SessionId))
                {
                    return BadRequest(new { error = "TripId and SessionId are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(request.SessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Validate trip and ownership
                var validationResult = await ValidateTripOwnership(tripId, session.DriverId);
                if (validationResult != null) return validationResult;

                var trip = await _firestoreService.GetTripAsync(tripId);
                if (trip!.Status != "in_progress")
                {
                    return BadRequest(new { error = $"Trip cannot be completed from current status: {trip.Status}" });
                }

                // TODO: In the future, we might want to validate that all deliveries are completed
                // For now, we'll allow completing trips without this validation

                // Complete the trip
                var completedAt = DateTime.UtcNow;
                await _firestoreService.UpdateTripStatusAsync(tripId, "completed", null, completedAt);
                
                // Update session last active
                await _firestoreService.UpdateSessionLastActiveAsync(request.SessionId, DateTime.UtcNow);

                _logger.LogInformation("Trip {TripId} completed by driver {DriverId}", tripId, session.DriverId);

                return Ok(new
                {
                    success = true,
                    message = "Trip completed successfully",
                    tripId = tripId,
                    driverId = session.DriverId,
                    status = "completed",
                    completedAt = completedAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error completing trip {TripId}", tripId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Release trip back to pool
        /// </summary>
        [HttpPost("{tripId}/release")]
        public async Task<IActionResult> ReleaseTrip(string tripId, [FromBody] TripActionRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(tripId) || string.IsNullOrEmpty(request.SessionId))
                {
                    return BadRequest(new { error = "TripId and SessionId are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(request.SessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Validate trip and ownership
                var validationResult = await ValidateTripOwnership(tripId, session.DriverId);
                if (validationResult != null) return validationResult;

                var trip = await _firestoreService.GetTripAsync(tripId);
                if (trip!.Status == "completed")
                {
                    return BadRequest(new { error = "Cannot release a completed trip" });
                }

                // Release the trip (clear session info and set status to available)
                await _firestoreService.ReleaseTripAsync(tripId);
                
                // Update session last active
                await _firestoreService.UpdateSessionLastActiveAsync(request.SessionId, DateTime.UtcNow);

                _logger.LogInformation("Trip {TripId} released by driver {DriverId}", tripId, session.DriverId);

                return Ok(new
                {
                    success = true,
                    message = "Trip released successfully",
                    tripId = tripId,
                    driverId = session.DriverId,
                    status = "available",
                    releasedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error releasing trip {TripId}", tripId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Helper method to validate trip ownership
        /// </summary>
        private async Task<IActionResult?> ValidateTripOwnership(string tripId, string driverId)
        {
            var trip = await _firestoreService.GetTripAsync(tripId);
            if (trip == null)
            {
                return NotFound(new { error = "Trip not found" });
            }

            if (trip.SessionInfo.DriverId != driverId)
            {
                return BadRequest(new { error = "Trip is not assigned to this driver" });
            }

            return null;
        }
    }

    // Request models for trip actions
    public class ClaimTripRequest
    {
        public string SessionId { get; set; } = string.Empty;
    }

    public class TripActionRequest
    {
        public string SessionId { get; set; } = string.Empty;
    }
}
