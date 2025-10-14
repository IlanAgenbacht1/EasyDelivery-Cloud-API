using Microsoft.AspNetCore.Mvc;
using DeliveryCloudApi.Services;
using DeliveryCloudApi.Models;

namespace DeliveryCloudApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class FilesController : ControllerBase
    {
        private readonly IFirestoreService _firestoreService;
        private readonly ILogger<FilesController> _logger;
        private readonly IWebHostEnvironment _environment;
        private readonly IConfiguration _configuration;

        // File upload limits and allowed types
        private const long MaxFileSize = 10 * 1024 * 1024; // 10MB
        private readonly string[] AllowedImageTypes = { "image/jpeg", "image/jpg", "image/png", "image/webp" };
        private readonly string[] AllowedDocumentTypes = { "application/pdf", "text/plain" };

        public FilesController(
            IFirestoreService firestoreService, 
            ILogger<FilesController> logger, 
            IWebHostEnvironment environment,
            IConfiguration configuration)
        {
            _firestoreService = firestoreService;
            _logger = logger;
            _environment = environment;
            _configuration = configuration;
        }

        /// <summary>
        /// General file upload endpoint
        /// </summary>
        [HttpPost("upload")]
        [RequestSizeLimit(MaxFileSize)]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> UploadFile([FromForm] FileUploadRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SessionId) || request.File == null)
                {
                    return BadRequest(new { error = "SessionId and File are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(request.SessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Validate file
                var validationResult = ValidateFile(request.File, request.FileType ?? "document");
                if (validationResult != null) return validationResult;

                // Generate unique file ID and name
                var fileId = Guid.NewGuid().ToString();
                var fileExtension = Path.GetExtension(request.File.FileName);
                var fileName = $"{fileId}{fileExtension}";
                
                // Create upload directory if it doesn't exist
                var uploadPath = GetUploadPath(session.ClientId);
                Directory.CreateDirectory(uploadPath);
                
                // Save file
                var filePath = Path.Combine(uploadPath, fileName);
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await request.File.CopyToAsync(stream);
                }

                // Create file metadata
                var fileMetadata = new FileMetadata
                {
                    FileName = fileName,
                    OriginalFileName = request.File.FileName,
                    ContentType = request.File.ContentType,
                    FileSize = request.File.Length,
                    FileType = request.FileType ?? "document",
                    UploadedBy = session.DriverId,
                    UploadedFrom = session.DeviceId,
                    ClientId = session.ClientId,
                    TripId = request.TripId ?? "",
                    DeliveryId = request.DeliveryId ?? "",
                    UploadedAt = DateTime.UtcNow,
                    FilePath = filePath,
                    PublicUrl = GenerateFileUrl(fileId)
                };

                // Save metadata to Firestore
                await _firestoreService.SaveFileMetadataAsync(fileId, fileMetadata);

                // Update session last active
                await _firestoreService.UpdateSessionLastActiveAsync(request.SessionId, DateTime.UtcNow);

                _logger.LogInformation("File {FileId} uploaded successfully by driver {DriverId}", 
                    fileId, session.DriverId);

                return Ok(new
                {
                    success = true,
                    message = "File uploaded successfully",
                    fileId = fileId,
                    fileName = fileName,
                    originalFileName = request.File.FileName,
                    fileSize = request.File.Length,
                    contentType = request.File.ContentType,
                    fileType = fileMetadata.FileType,
                    uploadedAt = fileMetadata.UploadedAt,
                    publicUrl = fileMetadata.PublicUrl
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading file");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Upload signature image
        /// </summary>
        [HttpPost("signature")]
        [RequestSizeLimit(MaxFileSize)]
        public async Task<IActionResult> UploadSignature([FromForm] SignatureUploadRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SessionId) || request.SignatureFile == null)
                {
                    return BadRequest(new { error = "SessionId and SignatureFile are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(request.SessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Validate signature file (must be image)
                var validationResult = ValidateFile(request.SignatureFile, "signature");
                if (validationResult != null) return validationResult;

                // Generate unique file ID and name
                var fileId = Guid.NewGuid().ToString();
                var fileName = $"signature_{fileId}.{GetFileExtension(request.SignatureFile.ContentType)}";
                
                // Create upload directory
                var uploadPath = GetUploadPath(session.ClientId, "signatures");
                Directory.CreateDirectory(uploadPath);
                
                // Save file
                var filePath = Path.Combine(uploadPath, fileName);
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await request.SignatureFile.CopyToAsync(stream);
                }

                // Create file metadata
                var fileMetadata = new FileMetadata
                {
                    FileName = fileName,
                    OriginalFileName = request.SignatureFile.FileName,
                    ContentType = request.SignatureFile.ContentType,
                    FileSize = request.SignatureFile.Length,
                    FileType = "signature",
                    UploadedBy = session.DriverId,
                    UploadedFrom = session.DeviceId,
                    ClientId = session.ClientId,
                    TripId = request.TripId ?? "",
                    DeliveryId = request.DeliveryId ?? "",
                    UploadedAt = DateTime.UtcNow,
                    FilePath = filePath,
                    PublicUrl = GenerateFileUrl(fileId)
                };

                await _firestoreService.SaveFileMetadataAsync(fileId, fileMetadata);
                await _firestoreService.UpdateSessionLastActiveAsync(request.SessionId, DateTime.UtcNow);

                _logger.LogInformation("Signature {FileId} uploaded successfully by driver {DriverId}", 
                    fileId, session.DriverId);

                return Ok(new
                {
                    success = true,
                    message = "Signature uploaded successfully",
                    fileId = fileId,
                    fileName = fileName,
                    fileSize = request.SignatureFile.Length,
                    uploadedAt = fileMetadata.UploadedAt,
                    signatureUrl = fileMetadata.PublicUrl
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading signature");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Upload POD photo
        /// </summary>
        [HttpPost("photo")]
        [RequestSizeLimit(MaxFileSize)]
        public async Task<IActionResult> UploadPhoto([FromForm] PhotoUploadRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SessionId) || request.PhotoFile == null)
                {
                    return BadRequest(new { error = "SessionId and PhotoFile are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(request.SessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Validate photo file (must be image)
                var validationResult = ValidateFile(request.PhotoFile, "photo");
                if (validationResult != null) return validationResult;

                // Generate unique file ID and name
                var fileId = Guid.NewGuid().ToString();
                var fileName = $"photo_{fileId}.{GetFileExtension(request.PhotoFile.ContentType)}";
                
                // Create upload directory
                var uploadPath = GetUploadPath(session.ClientId, "photos");
                Directory.CreateDirectory(uploadPath);
                
                // Save file
                var filePath = Path.Combine(uploadPath, fileName);
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await request.PhotoFile.CopyToAsync(stream);
                }

                // Create file metadata
                var fileMetadata = new FileMetadata
                {
                    FileName = fileName,
                    OriginalFileName = request.PhotoFile.FileName,
                    ContentType = request.PhotoFile.ContentType,
                    FileSize = request.PhotoFile.Length,
                    FileType = "photo",
                    UploadedBy = session.DriverId,
                    UploadedFrom = session.DeviceId,
                    ClientId = session.ClientId,
                    TripId = request.TripId ?? "",
                    DeliveryId = request.DeliveryId ?? "",
                    UploadedAt = DateTime.UtcNow,
                    FilePath = filePath,
                    PublicUrl = GenerateFileUrl(fileId)
                };

                await _firestoreService.SaveFileMetadataAsync(fileId, fileMetadata);
                await _firestoreService.UpdateSessionLastActiveAsync(request.SessionId, DateTime.UtcNow);

                _logger.LogInformation("Photo {FileId} uploaded successfully by driver {DriverId}", 
                    fileId, session.DriverId);

                return Ok(new
                {
                    success = true,
                    message = "Photo uploaded successfully",
                    fileId = fileId,
                    fileName = fileName,
                    fileSize = request.PhotoFile.Length,
                    uploadedAt = fileMetadata.UploadedAt,
                    photoUrl = fileMetadata.PublicUrl
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading photo");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Helper method to validate uploaded files
        /// </summary>
        private IActionResult? ValidateFile(IFormFile file, string fileType)
        {
            if (file.Length == 0)
            {
                return BadRequest(new { error = "File is empty" });
            }

            if (file.Length > MaxFileSize)
            {
                return BadRequest(new { error = $"File size exceeds maximum allowed size of {MaxFileSize / (1024 * 1024)}MB" });
            }

            // Validate content type based on file type
            var allowedTypes = fileType.ToLower() switch
            {
                "signature" => AllowedImageTypes,
                "photo" => AllowedImageTypes,
                "document" => AllowedDocumentTypes.Concat(AllowedImageTypes).ToArray(),
                _ => AllowedImageTypes
            };

            if (!allowedTypes.Contains(file.ContentType.ToLower()))
            {
                return BadRequest(new { 
                    error = $"Invalid file type. Allowed types for {fileType}: {string.Join(", ", allowedTypes)}" 
                });
            }

            return null;
        }

        /// <summary>
        /// Get upload path for client
        /// </summary>
        private string GetUploadPath(string clientId, string? subFolder = null)
        {
            var basePath = _configuration["FileUpload:BasePath"] ?? Path.Combine(_environment.WebRootPath, "uploads");
            var clientPath = Path.Combine(basePath, clientId);
            
            if (!string.IsNullOrEmpty(subFolder))
            {
                clientPath = Path.Combine(clientPath, subFolder);
            }
            
            return clientPath;
        }

        /// <summary>
        /// Generate public file URL
        /// </summary>
        private string GenerateFileUrl(string fileId)
        {
            var baseUrl = _configuration["FileUpload:BaseUrl"] ?? $"{Request.Scheme}://{Request.Host}";
            return $"{baseUrl}/api/files/{fileId}";
        }

        /// <summary>
        /// Get file extension from content type
        /// </summary>
        private string GetFileExtension(string contentType)
        {
            return contentType.ToLower() switch
            {
                "image/jpeg" => "jpg",
                "image/jpg" => "jpg",
                "image/png" => "png",
                "image/webp" => "webp",
                "application/pdf" => "pdf",
                "text/plain" => "txt",
                _ => "bin"
            };
        }

        /// <summary>
        /// Retrieve file by file ID
        /// </summary>
        [HttpGet("{fileId}")]
        public async Task<IActionResult> GetFile(string fileId, [FromQuery] string? sessionId = null)
        {
            try
            {
                if (string.IsNullOrEmpty(fileId))
                {
                    return BadRequest(new { error = "FileId is required" });
                }

                // Get file metadata
                var fileMetadata = await _firestoreService.GetFileMetadataAsync(fileId);
                if (fileMetadata == null || !fileMetadata.IsActive)
                {
                    return NotFound(new { error = "File not found or inactive" });
                }

                // If sessionId is provided, validate access and update session activity
                if (!string.IsNullOrEmpty(sessionId))
                {
                    var session = await _firestoreService.GetSessionAsync(sessionId);
                    if (session != null && session.IsActive)
                    {
                        // Verify client access - users can only access files from their client
                        if (session.ClientId != fileMetadata.ClientId)
                        {
                            return Forbid();
                        }
                        
                        await _firestoreService.UpdateSessionLastActiveAsync(sessionId, DateTime.UtcNow);
                    }
                }

                // Check if file exists on disk
                if (!System.IO.File.Exists(fileMetadata.FilePath))
                {
                    _logger.LogWarning("File {FileId} metadata exists but file not found at path: {FilePath}", 
                        fileId, fileMetadata.FilePath);
                    return NotFound(new { error = "File not found on storage" });
                }

                // Return file
                var fileStream = new FileStream(fileMetadata.FilePath, FileMode.Open, FileAccess.Read);
                return File(fileStream, fileMetadata.ContentType, fileMetadata.OriginalFileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving file {FileId}", fileId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get file metadata information
        /// </summary>
        [HttpGet("{fileId}/info")]
        public async Task<IActionResult> GetFileInfo(string fileId, [FromQuery] string sessionId)
        {
            try
            {
                if (string.IsNullOrEmpty(fileId) || string.IsNullOrEmpty(sessionId))
                {
                    return BadRequest(new { error = "FileId and SessionId are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(sessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Get file metadata
                var fileMetadata = await _firestoreService.GetFileMetadataAsync(fileId);
                if (fileMetadata == null || !fileMetadata.IsActive)
                {
                    return NotFound(new { error = "File not found or inactive" });
                }

                // Verify client access
                if (session.ClientId != fileMetadata.ClientId)
                {
                    return Forbid();
                }

                // Update session last active
                await _firestoreService.UpdateSessionLastActiveAsync(sessionId, DateTime.UtcNow);

                return Ok(new
                {
                    fileId = fileId,
                    fileName = fileMetadata.FileName,
                    originalFileName = fileMetadata.OriginalFileName,
                    contentType = fileMetadata.ContentType,
                    fileSize = fileMetadata.FileSize,
                    fileType = fileMetadata.FileType,
                    uploadedBy = fileMetadata.UploadedBy,
                    uploadedFrom = fileMetadata.UploadedFrom,
                    tripId = fileMetadata.TripId,
                    deliveryId = fileMetadata.DeliveryId,
                    uploadedAt = fileMetadata.UploadedAt,
                    publicUrl = fileMetadata.PublicUrl,
                    isActive = fileMetadata.IsActive
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting file info for {FileId}", fileId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Delete file with proper authorization
        /// </summary>
        [HttpDelete("{fileId}")]
        public async Task<IActionResult> DeleteFile(string fileId, [FromBody] DeleteFileRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(fileId) || string.IsNullOrEmpty(request.SessionId))
                {
                    return BadRequest(new { error = "FileId and SessionId are required" });
                }

                // Validate session
                var session = await _firestoreService.GetSessionAsync(request.SessionId);
                if (session == null || !session.IsActive)
                {
                    return BadRequest(new { error = "Invalid or inactive session" });
                }

                // Get file metadata
                var fileMetadata = await _firestoreService.GetFileMetadataAsync(fileId);
                if (fileMetadata == null)
                {
                    return NotFound(new { error = "File not found" });
                }

                // Verify authorization - only the uploader or same client can delete
                if (session.ClientId != fileMetadata.ClientId)
                {
                    return Forbid();
                }

                // Additional check: only allow deletion by the same driver or admin role
                // For now, we'll allow any driver from the same client to delete
                
                // Mark file as inactive (soft delete) instead of hard delete for audit purposes
                await _firestoreService.DeactivateFileAsync(fileId);

                // Optionally delete physical file (uncomment if you want hard delete)
                /*
                if (System.IO.File.Exists(fileMetadata.FilePath))
                {
                    System.IO.File.Delete(fileMetadata.FilePath);
                }
                */

                // Update session last active
                await _firestoreService.UpdateSessionLastActiveAsync(request.SessionId, DateTime.UtcNow);

                _logger.LogInformation("File {FileId} deleted by driver {DriverId}", 
                    fileId, session.DriverId);

                return Ok(new
                {
                    success = true,
                    message = "File deleted successfully",
                    fileId = fileId,
                    deletedAt = DateTime.UtcNow,
                    deletedBy = session.DriverId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting file {FileId}", fileId);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }
    }

    // Request models for file uploads
    public class FileUploadRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public IFormFile File { get; set; } = null!;
        public string? FileType { get; set; } // "signature", "photo", "document"
        public string? TripId { get; set; }
        public string? DeliveryId { get; set; }
    }

    public class SignatureUploadRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public IFormFile SignatureFile { get; set; } = null!;
        public string? TripId { get; set; }
        public string? DeliveryId { get; set; }
    }

    public class PhotoUploadRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public IFormFile PhotoFile { get; set; } = null!;
        public string? TripId { get; set; }
        public string? DeliveryId { get; set; }
    }

    public class DeleteFileRequest
    {
        public string SessionId { get; set; } = string.Empty;
    }
}
