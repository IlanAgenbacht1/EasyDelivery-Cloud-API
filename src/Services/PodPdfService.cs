using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using DeliveryCloudApi.Models;
using Newtonsoft.Json;

namespace DeliveryCloudApi.Services
{
    public interface IPodPdfService
    {
        Task<string> GeneratePodPdfAsync(string tripId, string deliveryId, string generatedBy = "system");
        Task<byte[]> GetPodPdfBytesAsync(string tripId, string deliveryId);
        Task<string?> GetExistingPodPathAsync(string tripId, string deliveryId);
    }

    public class PodPdfService : IPodPdfService
    {
        private readonly IFirestoreService _firestoreService;
        private readonly ILogger<PodPdfService> _logger;
        private readonly IWebHostEnvironment _environment;
        private readonly HttpClient _httpClient;

        public PodPdfService(
            IFirestoreService firestoreService,
            ILogger<PodPdfService> logger,
            IWebHostEnvironment environment,
            HttpClient httpClient)
        {
            _firestoreService = firestoreService;
            _logger = logger;
            _environment = environment;
            _httpClient = httpClient;

            // Configure QuestPDF license (Community license)
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public async Task<string> GeneratePodPdfAsync(string tripId, string deliveryId, string generatedBy = "system")
        {
            try
            {
                _logger.LogInformation("Starting PDF generation for delivery {DeliveryId} in trip {TripId}", deliveryId, tripId);

                // Get delivery data
                var delivery = await _firestoreService.GetDeliveryAsync(tripId, deliveryId);
                if (delivery == null)
                {
                    throw new ArgumentException($"Delivery {deliveryId} not found");
                }

                // Get trip data for additional info
                var trip = await _firestoreService.GetTripAsync(tripId);
                if (trip == null)
                {
                    throw new ArgumentException($"Trip {tripId} not found");
                }

                // Parse POD data to get file references
                var podData = ParsePodData(delivery.PodData);

                // Get signature and photo files
                byte[]? signatureBytes = null;
                byte[]? photoBytes = null;

                if (!string.IsNullOrEmpty(podData.SignatureUrl))
                {
                    signatureBytes = await GetFileBytes(podData.SignatureUrl);
                }

                if (!string.IsNullOrEmpty(podData.PhotoUrl))
                {
                    photoBytes = await GetFileBytes(podData.PhotoUrl);
                }

                // Create PDF document
                var document = Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Size(PageSizes.A4);
                        page.Margin(20);

                        page.Header().Element(ComposeHeader);
                        page.Content().Element(content => ComposeContent(content, delivery, trip, podData, signatureBytes, photoBytes));
                        page.Footer().Element(ComposeFooter);
                    });
                });

                // Generate PDF and save to file
                var fileName = $"POD_{deliveryId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf";
                var pdfDirectory = Path.Combine(_environment.WebRootPath ?? Directory.GetCurrentDirectory(), "pdfs", trip.ClientId);
                Directory.CreateDirectory(pdfDirectory);
                var filePath = Path.Combine(pdfDirectory, fileName);

                var pdfBytes = document.GeneratePdf();
                await File.WriteAllBytesAsync(filePath, pdfBytes);

                // Save POD document metadata
                var podDocument = new PodDocument
                {
                    DeliveryId = deliveryId,
                    TripId = tripId,
                    ClientId = trip.ClientId,
                    FileName = fileName,
                    FilePath = filePath,
                    FileSize = pdfBytes.Length,
                    GeneratedAt = DateTime.UtcNow,
                    GeneratedBy = generatedBy,
                    HasSignature = signatureBytes != null,
                    HasPhoto = photoBytes != null,
                    PublicUrl = $"/api/pods/{deliveryId}/pdf",
                    IsActive = true
                };

                var podDocumentId = $"{tripId}_{deliveryId}";
                await _firestoreService.SavePodDocumentAsync(podDocumentId, podDocument);

                _logger.LogInformation("PDF generated successfully for delivery {DeliveryId}: {FilePath} ({FileSize} bytes)",
                    deliveryId, filePath, pdfBytes.Length);

                return filePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating PDF for delivery {DeliveryId}", deliveryId);
                throw;
            }
        }

        public async Task<byte[]> GetPodPdfBytesAsync(string tripId, string deliveryId)
        {
            var podDocumentId = $"{tripId}_{deliveryId}";
            var podDocument = await _firestoreService.GetPodDocumentAsync(podDocumentId);

            if (podDocument == null || !podDocument.IsActive)
            {
                throw new FileNotFoundException($"POD document not found for delivery {deliveryId}");
            }

            if (!File.Exists(podDocument.FilePath))
            {
                throw new FileNotFoundException($"POD file not found at path: {podDocument.FilePath}");
            }

            return await File.ReadAllBytesAsync(podDocument.FilePath);
        }

        public async Task<string?> GetExistingPodPathAsync(string tripId, string deliveryId)
        {
            var podDocumentId = $"{tripId}_{deliveryId}";
            var podDocument = await _firestoreService.GetPodDocumentAsync(podDocumentId);

            if (podDocument?.IsActive == true && File.Exists(podDocument.FilePath))
            {
                return podDocument.FilePath;
            }

            return null;
        }

        private void ComposeHeader(IContainer container)
        {
            container.Row(row =>
            {
                row.RelativeItem().Column(column =>
                {
                    column.Item().Text("ELECTRONIC PROOF OF DELIVERY")
                        .FontSize(18)
                        .FontColor(Colors.Blue.Darken4)
                        .Bold();
                    
                    column.Item().Text("ePOD Certificate")
                        .FontSize(10)
                        .FontColor(Colors.Grey.Darken1);
                });

                row.ConstantItem(100).AlignRight().Text($"POD #{DateTime.UtcNow:yyyyMMdd}")
                    .FontSize(12)
                    .Bold();
            });

            container.PaddingTop(10).LineHorizontal(2).LineColor(Colors.Blue.Medium);
        }

        private void ComposeContent(IContainer container, Delivery delivery, Trip trip, PodData podData, 
            byte[]? signatureBytes, byte[]? photoBytes)
        {
            container.PaddingTop(15).Column(column =>
            {
                // Document and delivery info section
                column.Item().Row(row =>
                {
                    // Left column - Document info
                    row.RelativeItem().Padding(5).Border(1).BorderColor(Colors.Grey.Medium).Column(leftCol =>
                    {
                        leftCol.Item().Text("DOCUMENT INFO")
                            .FontSize(10)
                            .Bold()
                            .FontColor(Colors.Blue.Darken4);

                        leftCol.Item().PaddingTop(5).Row(infoRow =>
                        {
                            infoRow.ConstantItem(60).Text("Doc No:").FontSize(9).Bold();
                            infoRow.RelativeItem().Text(delivery.OrderNumber).FontSize(9);
                        });

                        if (!string.IsNullOrEmpty(delivery.OrderNumber))
                        {
                            leftCol.Item().Row(infoRow =>
                            {
                                infoRow.ConstantItem(60).Text("Order No:").FontSize(9).Bold();
                                infoRow.RelativeItem().Text(delivery.OrderNumber).FontSize(9);
                            });
                        }

                        leftCol.Item().Row(infoRow =>
                        {
                            infoRow.ConstantItem(60).Text("Date:").FontSize(9).Bold();
                            infoRow.RelativeItem().Text(podData.CompletedAt?.ToString("dd-MM-yyyy") ?? DateTime.UtcNow.ToString("dd-MM-yyyy")).FontSize(9);
                        });

                        leftCol.Item().Row(infoRow =>
                        {
                            infoRow.ConstantItem(60).Text("Time:").FontSize(9).Bold();
                            infoRow.RelativeItem().Text(podData.CompletedAt?.ToString("HH:mm:ss") ?? DateTime.UtcNow.ToString("HH:mm:ss")).FontSize(9);
                        });

                        // Delivery details
                        leftCol.Item().PaddingTop(10).Text("DELIVERY DETAILS")
                            .FontSize(10)
                            .Bold()
                            .FontColor(Colors.Blue.Darken4);

                        leftCol.Item().PaddingTop(5).Row(infoRow =>
                        {
                            infoRow.ConstantItem(60).Text("Company:").FontSize(9).Bold();
                            infoRow.RelativeItem().Text(delivery.Customer.Name).FontSize(9);
                        });

                        if (!string.IsNullOrEmpty(trip.SessionInfo.DriverId))
                        {
                            leftCol.Item().Row(infoRow =>
                            {
                                infoRow.ConstantItem(60).Text("Driver:").FontSize(9).Bold();
                                infoRow.RelativeItem().Text(trip.SessionInfo.DriverId).FontSize(9);
                            });
                        }

                        if (!string.IsNullOrEmpty(trip.SessionInfo.VehicleId))
                        {
                            leftCol.Item().Row(infoRow =>
                            {
                                infoRow.ConstantItem(60).Text("Vehicle:").FontSize(9).Bold();
                                infoRow.RelativeItem().Text(trip.SessionInfo.VehicleId).FontSize(9);
                            });
                        }

                        if (!string.IsNullOrEmpty(delivery.Customer.Contact))
                        {
                            leftCol.Item().Row(infoRow =>
                            {
                                infoRow.ConstantItem(60).Text("Contact:").FontSize(9).Bold();
                                infoRow.RelativeItem().Text(delivery.Customer.Contact).FontSize(9);
                            });
                        }

                        if (!string.IsNullOrEmpty(delivery.Customer.Number))
                        {
                            leftCol.Item().Row(infoRow =>
                            {
                                infoRow.ConstantItem(60).Text("Phone:").FontSize(9).Bold();
                                infoRow.RelativeItem().Text(delivery.Customer.Number).FontSize(9);
                            });
                        }

                        if (!string.IsNullOrEmpty(delivery.Address))
                        {
                            leftCol.Item().Row(infoRow =>
                            {
                                infoRow.ConstantItem(60).Text("Address:").FontSize(8).Bold();
                                infoRow.RelativeItem().Text(delivery.Address).FontSize(8);
                            });
                        }
                    });

                    // Right column - Parcel info
                    row.RelativeItem().PaddingLeft(10).Padding(5).Border(1).BorderColor(Colors.Grey.Medium).Column(rightCol =>
                    {
                        rightCol.Item().Text("PARCEL INFO")
                            .FontSize(10)
                            .Bold()
                            .FontColor(Colors.Blue.Darken4);

                        rightCol.Item().PaddingTop(5).Row(infoRow =>
                        {
                            infoRow.ConstantItem(60).Text("Expected:").FontSize(9).Bold();
                            infoRow.RelativeItem().Text(delivery.QuantityExpected).FontSize(9);
                        });

                        if (!string.IsNullOrEmpty(podData.Quantity))
                        {
                            rightCol.Item().Row(infoRow =>
                            {
                                infoRow.ConstantItem(60).Text("Delivered:").FontSize(9).Bold();
                                infoRow.RelativeItem().Text(podData.Quantity).FontSize(9);
                            });
                        }

                        if (!string.IsNullOrEmpty(podData.Comment))
                        {
                            rightCol.Item().PaddingTop(5).Row(infoRow =>
                            {
                                infoRow.ConstantItem(60).Text("Notes:").FontSize(8).Bold();
                                infoRow.RelativeItem().Text(podData.Comment).FontSize(8);
                            });
                        }

                        if (!string.IsNullOrEmpty(podData.RecipientName))
                        {
                            rightCol.Item().Row(infoRow =>
                            {
                                infoRow.ConstantItem(60).Text("Recipient:").FontSize(9).Bold();
                                infoRow.RelativeItem().Text(podData.RecipientName).FontSize(9);
                            });
                        }
                    });
                });

                // Evidence section
                column.Item().PaddingTop(15).Border(1).BorderColor(Colors.Grey.Medium).Padding(10).Column(evidenceCol =>
                {
                    evidenceCol.Item().AlignCenter().Text("DELIVERY EVIDENCE")
                        .FontSize(12)
                        .Bold()
                        .FontColor(Colors.Blue.Darken4);

                    evidenceCol.Item().PaddingTop(10).Row(evidenceRow =>
                    {
                        // Signature section
                        evidenceRow.RelativeItem().Border(1).BorderColor(Colors.Grey.Lighten1).Padding(10).Column(sigCol =>
                        {
                            sigCol.Item().AlignCenter().Text("Customer Signature")
                                .FontSize(10)
                                .Bold();

                            if (signatureBytes != null)
                            {
                                sigCol.Item().PaddingTop(10).Height(80).AlignCenter().Image(signatureBytes);
                            }
                            else
                            {
                                sigCol.Item().PaddingTop(20).Height(60).AlignCenter()
                                    .Border(2, Unit.Point).BorderColor(Colors.Grey.Lighten1)
                                    .Background(Colors.Grey.Lighten3)
                                    .AlignCenter().AlignMiddle()
                                    .Text("No signature captured").FontSize(9).FontColor(Colors.Grey.Medium);
                            }
                        });

                        // Photo section
                        evidenceRow.RelativeItem().PaddingLeft(10).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(10).Column(photoCol =>
                        {
                            photoCol.Item().AlignCenter().Text("Delivery Photo")
                                .FontSize(10)
                                .Bold();

                            if (photoBytes != null)
                            {
                                photoCol.Item().PaddingTop(10).Height(80).AlignCenter().Image(photoBytes);
                            }
                            else
                            {
                                photoCol.Item().PaddingTop(20).Height(60).AlignCenter()
                                    .Border(2, Unit.Point).BorderColor(Colors.Grey.Lighten1)
                                    .Background(Colors.Grey.Lighten3)
                                    .AlignCenter().AlignMiddle()
                                    .Text("No photo available").FontSize(9).FontColor(Colors.Grey.Medium);
                            }
                        });
                    });
                });

                // GPS Location if available
                if (!string.IsNullOrEmpty(podData.CapturedGpsLocation) && podData.CapturedGpsLocation != "[0° N, 0° E]")
                {
                    column.Item().PaddingTop(10).Row(gpsRow =>
                    {
                        gpsRow.ConstantItem(80).Text("GPS Location:").FontSize(9).Bold();
                        gpsRow.RelativeItem().Text(podData.CapturedGpsLocation).FontSize(9);
                    });
                }
            });
        }

        private void ComposeFooter(IContainer container)
        {
            container.Border(1).BorderColor(Colors.Blue.Medium).Padding(8).AlignCenter().Column(column =>
            {
                column.Item().Text("This ePOD certifies successful delivery completion")
                    .FontSize(11)
                    .Bold()
                    .FontColor(Colors.Blue.Darken4);

                column.Item().Text($"Generated by EasyDelivery System • {DateTime.UtcNow:dd-MM-yyyy HH:mm:ss}")
                    .FontSize(9)
                    .FontColor(Colors.Grey.Darken1);

                column.Item().Text("Electronically generated - no physical signature required")
                    .FontSize(8)
                    .FontColor(Colors.Grey.Medium);
            });
        }

        private async Task<byte[]?> GetFileBytes(string fileUrl)
        {
            try
            {
                if (string.IsNullOrEmpty(fileUrl))
                {
                    return null;
                }

                // Check if it's a Firebase Storage URL (direct download)
                if (fileUrl.StartsWith("https://firebasestorage.googleapis.com/") || 
                    fileUrl.StartsWith("https://storage.googleapis.com/"))
                {
                    // Download directly from Firebase Storage using injected HttpClient
                    var response = await _httpClient.GetAsync(fileUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        return await response.Content.ReadAsByteArrayAsync();
                    }
                    else
                    {
                        _logger.LogWarning("Failed to download file from Firebase URL: {FileUrl}, Status: {StatusCode}", 
                            fileUrl, response.StatusCode);
                        return null;
                    }
                }
                else
                {
                    // Legacy: Handle local API file URLs (for backward compatibility)
                    var fileId = fileUrl.Split('/').LastOrDefault();
                    if (string.IsNullOrEmpty(fileId))
                    {
                        return null;
                    }

                    var fileMetadata = await _firestoreService.GetFileMetadataAsync(fileId);
                    if (fileMetadata?.IsActive == true && File.Exists(fileMetadata.FilePath))
                    {
                        return await File.ReadAllBytesAsync(fileMetadata.FilePath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load file from URL: {FileUrl}", fileUrl);
            }

            return null;
        }

        private PodData ParsePodData(string podDataJson)
        {
            try
            {
                if (string.IsNullOrEmpty(podDataJson))
                {
                    return new PodData();
                }

                var podData = JsonConvert.DeserializeObject<PodData>(podDataJson);
                return podData ?? new PodData();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing POD data JSON: {PodDataJson}", podDataJson);
                return new PodData();
            }
        }
    }

    // Helper class for POD data parsing
    public class PodData
    {
        public string SignatureUrl { get; set; } = string.Empty;
        public string CapturedGpsLocation { get; set; } = string.Empty;
        public string PhotoUrl { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public string RecipientName { get; set; } = string.Empty;
        public string Quantity { get; set; } = string.Empty;
        public DateTime? CompletedAt { get; set; }
        public string CompletedBy { get; set; } = string.Empty;
    }
}