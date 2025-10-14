using Microsoft.AspNetCore.Mvc;
using DeliveryCloudApi.Services;
using DeliveryCloudApi.Models;

namespace DeliveryCloudApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class AuthController : ControllerBase
    {
        private readonly IFirestoreService _firestoreService;
        private readonly ILogger<AuthController> _logger;

        public AuthController(IFirestoreService firestoreService, ILogger<AuthController> logger)
        {
            _firestoreService = firestoreService;
            _logger = logger;
        }

        /// <summary>
        /// Validate enrollment key against client - devices are automatically approved upon successful validation
        /// </summary>
        [HttpPost("validate-key")]
        public async Task<IActionResult> ValidateEnrollmentKey([FromBody] ValidateKeyRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.EnrollmentKey) || string.IsNullOrEmpty(request.DeviceId))
                {
                    return BadRequest(new { error = "EnrollmentKey and DeviceId are required" });
                }

                var client = await _firestoreService.ValidateEnrollmentKeyAsync(request.EnrollmentKey);
                if (client == null)
                {
                    _logger.LogWarning("Invalid enrollment key attempted: {EnrollmentKey}", request.EnrollmentKey);
                    return BadRequest(new { error = "Invalid or expired enrollment key" });
                }

                // Check if client has reached device limit
                var registeredDevices = await _firestoreService.GetClientDeviceCountAsync(client.ClientId);
                if (registeredDevices >= client.DeviceLimit)
                {
                    _logger.LogWarning("Device limit reached for client {ClientId}. Current: {Current}, Limit: {Limit}", 
                        client.ClientId, registeredDevices, client.DeviceLimit);
                    return BadRequest(new { error = "Device limit reached for this client", limit = client.DeviceLimit });
                }

                return Ok(new 
                {
                    valid = true,
                    clientId = client.ClientId,
                    clientName = client.ClientName,
                    devicesRegistered = registeredDevices,
                    deviceLimit = client.DeviceLimit
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating enrollment key");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Register new device with client - automatically approved with valid enrollment key
        /// </summary>
        [HttpPost("register-device")]
        public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.EnrollmentKey) || 
                    string.IsNullOrEmpty(request.DeviceId) || 
                    string.IsNullOrEmpty(request.DeviceType))
                {
                    return BadRequest(new { error = "EnrollmentKey, DeviceId, and DeviceType are required" });
                }

                // Validate enrollment key again
                var client = await _firestoreService.ValidateEnrollmentKeyAsync(request.EnrollmentKey);
                if (client == null)
                {
                    return BadRequest(new { error = "Invalid or expired enrollment key" });
                }

                // Check if device already exists
                var existingDevice = await _firestoreService.GetDeviceAsync(request.DeviceId);
                if (existingDevice != null)
                {
                    return BadRequest(new { error = "Device already registered", 
                        isApproved = existingDevice.IsApproved });
                }

                // Create device record - automatically approved since enrollment key is valid
                var device = new Device
                {
                    ClientId = client.ClientId,
                    DeviceType = request.DeviceType,
                    IsApproved = true, // Automatically approved with valid enrollment key
                    RegisteredAt = DateTime.UtcNow,
                    LastSeenAt = DateTime.UtcNow
                };

                await _firestoreService.CreateDeviceAsync(request.DeviceId, device);

                _logger.LogInformation("Device {DeviceId} registered for client {ClientId}", 
                    request.DeviceId, client.ClientId);

                return Ok(new 
                {
                    success = true,
                    message = "Device registered and approved successfully.",
                    deviceId = request.DeviceId,
                    clientId = client.ClientId,
                    isApproved = true,
                    registeredAt = device.RegisteredAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering device");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Check if device is approved for use
        /// </summary>
        [HttpGet("device-status")]
        public async Task<IActionResult> GetDeviceStatus([FromQuery] string deviceId)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId))
                {
                    return BadRequest(new { error = "DeviceId is required" });
                }

                var device = await _firestoreService.GetDeviceAsync(deviceId);
                if (device == null)
                {
                    return NotFound(new { error = "Device not found" });
                }

                // Update last seen timestamp
                await _firestoreService.UpdateDeviceLastSeenAsync(deviceId, DateTime.UtcNow);

                return Ok(new 
                {
                    deviceId = deviceId,
                    clientId = device.ClientId,
                    deviceType = device.DeviceType,
                    isApproved = device.IsApproved,
                    registeredAt = device.RegisteredAt,
                    lastSeenAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking device status");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get available drivers for client
        /// </summary>
        [HttpGet("drivers")]
        public async Task<IActionResult> GetDrivers([FromQuery] string clientId)
        {
            try
            {
                if (string.IsNullOrEmpty(clientId))
                {
                    return BadRequest(new { error = "ClientId is required" });
                }

                var drivers = await _firestoreService.GetClientDriversAsync(clientId);
                var driverList = drivers.Select(d => new 
                {
                    driverId = d.Key,
                    name = d.Value.Name,
                    isActive = d.Value.IsActive,
                    hasPinSetup = !string.IsNullOrEmpty(d.Value.PinHash),
                    createdAt = d.Value.CreatedAt
                });

                return Ok(new 
                {
                    clientId = clientId,
                    drivers = driverList,
                    count = driverList.Count()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting drivers for client {ClientId}", clientId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Setup PIN for an existing driver
        /// </summary>
        [HttpPost("setup-pin")]
        public async Task<IActionResult> SetupDriverPin([FromBody] SetupPinRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.DriverId) || string.IsNullOrEmpty(request.Pin))
                {
                    return BadRequest(new { error = "DriverId and Pin are required" });
                }

                if (request.Pin.Length != 4 || !request.Pin.All(char.IsDigit))
                {
                    return BadRequest(new { error = "PIN must be exactly 4 digits" });
                }

                var driver = await _firestoreService.GetDriverAsync(request.DriverId);
                if (driver == null)
                {
                    return NotFound(new { error = "Driver not found" });
                }

                if (!driver.IsActive)
                {
                    return BadRequest(new { error = "Driver account is inactive" });
                }

                // Hash the PIN (using BCrypt for security)
                var pinHash = BCrypt.Net.BCrypt.HashPassword(request.Pin);

                await _firestoreService.UpdateDriverPinAsync(request.DriverId, pinHash);

                _logger.LogInformation("PIN setup completed for driver {DriverId}", request.DriverId);

                return Ok(new 
                {
                    success = true,
                    message = "PIN setup completed successfully",
                    driverId = request.DriverId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting up PIN for driver {DriverId}", request.DriverId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Authenticate driver with PIN
        /// </summary>
        [HttpPost("login")]
        public async Task<IActionResult> LoginDriver([FromBody] LoginRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.DriverId) || 
                    string.IsNullOrEmpty(request.Pin) ||
                    string.IsNullOrEmpty(request.DeviceId))
                {
                    return BadRequest(new { error = "DriverId, Pin, and DeviceId are required" });
                }

                // Verify device is approved
                var device = await _firestoreService.GetDeviceAsync(request.DeviceId);
                if (device == null || !device.IsApproved)
                {
                    return BadRequest(new { error = "Device not found or not approved" });
                }

                // Get and verify driver
                var driver = await _firestoreService.GetDriverAsync(request.DriverId);
                if (driver == null)
                {
                    return BadRequest(new { error = "Invalid credentials" });
                }

                if (!driver.IsActive)
                {
                    return BadRequest(new { error = "Driver account is inactive" });
                }

                if (string.IsNullOrEmpty(driver.PinHash))
                {
                    return BadRequest(new { error = "PIN not set up for this driver" });
                }

                // Verify PIN
                if (!BCrypt.Net.BCrypt.Verify(request.Pin, driver.PinHash))
                {
                    _logger.LogWarning("Invalid PIN attempt for driver {DriverId} from device {DeviceId}", 
                        request.DriverId, request.DeviceId);
                    return BadRequest(new { error = "Invalid credentials" });
                }

                // Verify driver and device belong to same client
                if (driver.ClientId != device.ClientId)
                {
                    _logger.LogWarning("Client mismatch: Driver {DriverId} (client: {DriverClient}) attempted login from device {DeviceId} (client: {DeviceClient})", 
                        request.DriverId, driver.ClientId, request.DeviceId, device.ClientId);
                    return BadRequest(new { error = "Invalid device for this driver" });
                }

                _logger.LogInformation("Driver {DriverId} authenticated successfully from device {DeviceId}", 
                    request.DriverId, request.DeviceId);

                return Ok(new 
                {
                    success = true,
                    message = "Authentication successful",
                    driverId = request.DriverId,
                    driverName = driver.Name,
                    clientId = driver.ClientId,
                    deviceId = request.DeviceId,
                    authenticationTime = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during driver login");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get available vehicles for client
        /// </summary>
        [HttpGet("vehicles")]
        public async Task<IActionResult> GetVehicles([FromQuery] string clientId)
        {
            try
            {
                if (string.IsNullOrEmpty(clientId))
                {
                    return BadRequest(new { error = "ClientId is required" });
                }

                var vehicles = await _firestoreService.GetClientVehiclesAsync(clientId);
                var vehicleList = vehicles.Select(v => new 
                {
                    vehicleId = v.Key,
                    registrationNumber = v.Value.RegistrationNumber,
                    createdAt = v.Value.CreatedAt
                });

                return Ok(new 
                {
                    clientId = clientId,
                    vehicles = vehicleList,
                    count = vehicleList.Count()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting vehicles for client {ClientId}", clientId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Start driver session with vehicle selection
        /// </summary>
        [HttpPost("start-session")]
        public async Task<IActionResult> StartSession([FromBody] StartSessionRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.DriverId) || 
                    string.IsNullOrEmpty(request.DeviceId) ||
                    string.IsNullOrEmpty(request.VehicleId))
                {
                    return BadRequest(new { error = "DriverId, DeviceId, and VehicleId are required" });
                }

                // Verify driver exists and is authenticated (this would be checked by middleware in production)
                var driver = await _firestoreService.GetDriverAsync(request.DriverId);
                if (driver == null || !driver.IsActive)
                {
                    return BadRequest(new { error = "Driver not found or inactive" });
                }

                // Verify device is approved
                var device = await _firestoreService.GetDeviceAsync(request.DeviceId);
                if (device == null || !device.IsApproved)
                {
                    return BadRequest(new { error = "Device not found or not approved" });
                }

                // Verify vehicle exists and belongs to same client
                var vehicle = await _firestoreService.GetVehicleAsync(request.VehicleId);
                if (vehicle == null)
                {
                    return BadRequest(new { error = "Vehicle not found" });
                }

                if (driver.ClientId != device.ClientId || driver.ClientId != vehicle.ClientId)
                {
                    return BadRequest(new { error = "Driver, device, and vehicle must belong to the same client" });
                }

                // Check if there's already an active session for this driver
                var existingSession = await _firestoreService.GetActiveDriverSessionAsync(request.DriverId);
                if (existingSession != null)
                {
                    return BadRequest(new { error = "Driver already has an active session", 
                        sessionId = existingSession.Value.Key });
                }

                // Check if vehicle is already in use
                var vehicleInUse = await _firestoreService.IsVehicleInUseAsync(request.VehicleId);
                if (vehicleInUse)
                {
                    return BadRequest(new { error = "Vehicle is already in use by another driver" });
                }

                // Create new session
                var sessionId = Guid.NewGuid().ToString();
                var session = new Session
                {
                    DriverId = request.DriverId,
                    DeviceId = request.DeviceId,
                    ClientId = driver.ClientId,
                    VehicleId = request.VehicleId,
                    CreatedAt = DateTime.UtcNow,
                    LastActiveAt = DateTime.UtcNow,
                    IsActive = true
                };

                await _firestoreService.CreateSessionAsync(sessionId, session);

                _logger.LogInformation("Session {SessionId} started for driver {DriverId} with vehicle {VehicleId}", 
                    sessionId, request.DriverId, request.VehicleId);

                return Ok(new 
                {
                    success = true,
                    message = "Session started successfully",
                    sessionId = sessionId,
                    driverId = request.DriverId,
                    driverName = driver.Name,
                    deviceId = request.DeviceId,
                    vehicleId = request.VehicleId,
                    vehicleRegistration = vehicle.RegistrationNumber,
                    clientId = driver.ClientId,
                    createdAt = session.CreatedAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting session");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// End driver session
        /// </summary>
        [HttpPut("end-session")]
        public async Task<IActionResult> EndSession([FromBody] EndSessionRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SessionId))
                {
                    return BadRequest(new { error = "SessionId is required" });
                }

                var session = await _firestoreService.GetSessionAsync(request.SessionId);
                if (session == null)
                {
                    return NotFound(new { error = "Session not found" });
                }

                if (!session.IsActive)
                {
                    return BadRequest(new { error = "Session is already inactive" });
                }

                await _firestoreService.EndSessionAsync(request.SessionId);

                _logger.LogInformation("Session {SessionId} ended for driver {DriverId}", 
                    request.SessionId, session.DriverId);

                return Ok(new 
                {
                    success = true,
                    message = "Session ended successfully",
                    sessionId = request.SessionId,
                    driverId = session.DriverId,
                    endedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ending session");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get current session information
        /// </summary>
        [HttpGet("session")]
        public async Task<IActionResult> GetSession([FromQuery] string sessionId)
        {
            try
            {
                if (string.IsNullOrEmpty(sessionId))
                {
                    return BadRequest(new { error = "SessionId is required" });
                }

                var session = await _firestoreService.GetSessionAsync(sessionId);
                if (session == null)
                {
                    return NotFound(new { error = "Session not found" });
                }

                // Update last active timestamp
                if (session.IsActive)
                {
                    await _firestoreService.UpdateSessionLastActiveAsync(sessionId, DateTime.UtcNow);
                }

                return Ok(new 
                {
                    sessionId = sessionId,
                    driverId = session.DriverId,
                    deviceId = session.DeviceId,
                    clientId = session.ClientId,
                    vehicleId = session.VehicleId,
                    isActive = session.IsActive,
                    createdAt = session.CreatedAt,
                    lastActiveAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting session information");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }
    }

    // Request models for device authentication
    public class ValidateKeyRequest
    {
        public string EnrollmentKey { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
    }

    public class RegisterDeviceRequest
    {
        public string EnrollmentKey { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceType { get; set; } = string.Empty; // "tablet", "phone", etc.
    }

    // Request models for driver authentication
    public class SetupPinRequest
    {
        public string DriverId { get; set; } = string.Empty;
        public string Pin { get; set; } = string.Empty;
    }

    public class LoginRequest
    {
        public string DriverId { get; set; } = string.Empty;
        public string Pin { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
    }

    // Request models for session management
    public class StartSessionRequest
    {
        public string DriverId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string VehicleId { get; set; } = string.Empty;
    }

    public class EndSessionRequest
    {
        public string SessionId { get; set; } = string.Empty;
    }
}
