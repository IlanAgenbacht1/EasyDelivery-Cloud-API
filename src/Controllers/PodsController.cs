using Microsoft.AspNetCore.Mvc;
using DeliveryCloudApi.Services;

namespace DeliveryCloudApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class PodsController : ControllerBase
    {
        private readonly IPodPdfService _podPdfService;
        private readonly IFirestoreService _firestoreService;
        private readonly ILogger<PodsController> _logger;

        public PodsController(
            IPodPdfService podPdfService,
            IFirestoreService firestoreService,
            ILogger<PodsController> logger)
        {
            _podPdfService = podPdfService;
            _firestoreService = firestoreService;
            _logger = logger;
        }

        /// <summary>
        /// Generate POD PDF for a specific delivery
        /// </summary>
        [HttpPost("generate/{deliveryId}")]
        public async Task<IActionResult> GeneratePodPdf(string deliveryId, [FromBody] GeneratePodRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(deliveryId) || string.IsNullOrEmpty(request.TripId))
                {
                    return BadRequest(new { error = "DeliveryId and TripId are required" });
                }

                // Optional session validation - if sessionId provided, validate it
                string generatedBy = "system";
                if (!string.IsNullOrEmpty(request.SessionId))
                {
                    var session = await _firestoreService.GetSessionAsync(request.SessionId);
                    if (session != null && session.IsActive)
                    {
                        generatedBy = session.DriverId;
                        await _firestoreService.UpdateSessionLastActiveAsync(request.SessionId, DateTime.UtcNow);
                    }
                }

                // Generate PDF
                var pdfPath = await _podPdfService.GeneratePodPdfAsync(request.TripId, deliveryId, generatedBy);

                _logger.LogInformation("POD PDF generated for delivery {DeliveryId} by {GeneratedBy}", 
                    deliveryId, generatedBy);

                return Ok(new
                {
                    success = true,
                    message = "POD PDF generated successfully",
                    deliveryId = deliveryId,
                    tripId = request.TripId,
                    generatedBy = generatedBy,
                    generatedAt = DateTime.UtcNow,
                    downloadUrl = $"/api/pods/{deliveryId}/pdf?tripId={request.TripId}"
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating POD PDF for delivery {DeliveryId}", deliveryId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Download POD PDF for a specific delivery
        /// </summary>
        [HttpGet("{deliveryId}/pdf")]
        public async Task<IActionResult> GetPodPdf(string deliveryId, [FromQuery] string tripId)
        {
            try
            {
                if (string.IsNullOrEmpty(deliveryId) || string.IsNullOrEmpty(tripId))
                {
                    return BadRequest(new { error = "DeliveryId and TripId are required" });
                }

                // Try to get existing PDF first
                var existingPdfPath = await _podPdfService.GetExistingPodPathAsync(tripId, deliveryId);
                
                if (string.IsNullOrEmpty(existingPdfPath))
                {
                    // Generate PDF if it doesn't exist
                    existingPdfPath = await _podPdfService.GeneratePodPdfAsync(tripId, deliveryId, "system");
                }

                // Get PDF bytes
                var pdfBytes = await _podPdfService.GetPodPdfBytesAsync(tripId, deliveryId);
                var fileName = $"POD_{deliveryId}.pdf";

                return File(pdfBytes, "application/pdf", fileName);
            }
            catch (FileNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving POD PDF for delivery {DeliveryId}", deliveryId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get POD document information/status
        /// </summary>
        [HttpGet("{deliveryId}/info")]
        public async Task<IActionResult> GetPodInfo(string deliveryId, [FromQuery] string tripId, [FromQuery] string? sessionId = null)
        {
            try
            {
                if (string.IsNullOrEmpty(deliveryId) || string.IsNullOrEmpty(tripId))
                {
                    return BadRequest(new { error = "DeliveryId and TripId are required" });
                }

                // Optional session validation
                if (!string.IsNullOrEmpty(sessionId))
                {
                    var session = await _firestoreService.GetSessionAsync(sessionId);
                    if (session != null && session.IsActive)
                    {
                        await _firestoreService.UpdateSessionLastActiveAsync(sessionId, DateTime.UtcNow);
                    }
                }

                var podDocumentId = $"{tripId}_{deliveryId}";
                var podDocument = await _firestoreService.GetPodDocumentAsync(podDocumentId);

                if (podDocument == null || !podDocument.IsActive)
                {
                    return Ok(new
                    {
                        deliveryId = deliveryId,
                        tripId = tripId,
                        hasPodDocument = false,
                        message = "POD document not generated yet"
                    });
                }

                return Ok(new
                {
                    deliveryId = deliveryId,
                    tripId = tripId,
                    hasPodDocument = true,
                    fileName = podDocument.FileName,
                    fileSize = podDocument.FileSize,
                    generatedAt = podDocument.GeneratedAt,
                    generatedBy = podDocument.GeneratedBy,
                    hasSignature = podDocument.HasSignature,
                    hasPhoto = podDocument.HasPhoto,
                    downloadUrl = $"/api/pods/{deliveryId}/pdf?tripId={tripId}",
                    isActive = podDocument.IsActive
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting POD info for delivery {DeliveryId}", deliveryId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Regenerate POD PDF (useful if delivery data was updated)
        /// </summary>
        [HttpPost("{deliveryId}/regenerate")]
        public async Task<IActionResult> RegeneratePodPdf(string deliveryId, [FromBody] RegeneratePodRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(deliveryId) || string.IsNullOrEmpty(request.TripId) || string.IsNullOrEmpty(request.SessionId))
                {
                    return BadRequest(new { error = "DeliveryId, TripId, and SessionId are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(request.SessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Regenerate PDF (this will overwrite existing one)
                var pdfPath = await _podPdfService.GeneratePodPdfAsync(request.TripId, deliveryId, session.DriverId);

                await _firestoreService.UpdateSessionLastActiveAsync(request.SessionId, DateTime.UtcNow);

                _logger.LogInformation("POD PDF regenerated for delivery {DeliveryId} by driver {DriverId}", 
                    deliveryId, session.DriverId);

                return Ok(new
                {
                    success = true,
                    message = "POD PDF regenerated successfully",
                    deliveryId = deliveryId,
                    tripId = request.TripId,
                    regeneratedBy = session.DriverId,
                    regeneratedAt = DateTime.UtcNow,
                    downloadUrl = $"/api/pods/{deliveryId}/pdf?tripId={request.TripId}"
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error regenerating POD PDF for delivery {DeliveryId}", deliveryId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }
    }

    // Request models for POD operations
    public class GeneratePodRequest
    {
        public string TripId { get; set; } = string.Empty;
        public string? SessionId { get; set; } // Optional for system-generated PDFs
    }

    public class RegeneratePodRequest
    {
        public string TripId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
    }
}