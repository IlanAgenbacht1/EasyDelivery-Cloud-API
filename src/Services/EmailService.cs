using System.Net.Mail;
using System.Net;
using DeliveryCloudApi.Models;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace DeliveryCloudApi.Services
{
    public interface IEmailService
    {
        Task<bool> SendPodEmailsAsync(string tripId, string deliveryId, string initiatedBy = "system");
        Task<bool> SendEmailAsync(string recipient, string subject, string body, byte[]? attachmentData = null, string? attachmentName = null, string emailType = "general");
    }

    public class EmailService : IEmailService
    {
        private readonly IFirestoreService _firestoreService;
        private readonly IPodPdfService _podPdfService;
        private readonly ILogger<EmailService> _logger;
        private readonly EmailConfig _emailConfig;

        public EmailService(
            IFirestoreService firestoreService,
            IPodPdfService podPdfService,
            ILogger<EmailService> logger,
            IConfiguration configuration)
        {
            _firestoreService = firestoreService;
            _podPdfService = podPdfService;
            _logger = logger;
            _emailConfig = configuration.GetSection("Email").Get<EmailConfig>() ?? new EmailConfig();
        }

        public async Task<bool> SendPodEmailsAsync(string tripId, string deliveryId, string initiatedBy = "system")
        {
            try
            {
                _logger.LogInformation("Starting to send POD emails for delivery {DeliveryId}", deliveryId);

                // Get delivery and trip data
                var delivery = await _firestoreService.GetDeliveryAsync(tripId, deliveryId);
                var trip = await _firestoreService.GetTripAsync(tripId);

                if (delivery == null || trip == null)
                {
                    _logger.LogError("Delivery {DeliveryId} or trip {TripId} not found", deliveryId, tripId);
                    return false;
                }

                // Get POD PDF bytes
                byte[]? podPdfBytes = null;
                try
                {
                    podPdfBytes = await _podPdfService.GetPodPdfBytesAsync(tripId, deliveryId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not get POD PDF for delivery {DeliveryId}", deliveryId);
                }

                var podAttachmentName = $"POD_{deliveryId}.pdf";
                bool allEmailsSuccessful = true;

                // 1. Send to customer
                if (!string.IsNullOrEmpty(delivery.Customer.Email))
                {
                    var customerSubject = $"Delivery Confirmation - {delivery.OrderNumber}";
                    var customerBody = GenerateCustomerEmailTemplate(delivery, trip);
                    
                    var customerResult = await SendEmailAsync(
                        delivery.Customer.Email,
                        customerSubject,
                        customerBody,
                        podPdfBytes,
                        podAttachmentName,
                        "customer"
                    );
                    
                    if (!customerResult)
                    {
                        allEmailsSuccessful = false;
                    }
                }

                // 2. Send to client/admin
                var client = await _firestoreService.GetClientAsync(trip.ClientId);
                if (client != null && !string.IsNullOrEmpty(client.PodEmail))
                {
                    var adminSubject = $"POD Generated - {delivery.OrderNumber} ({deliveryId})";
                    var adminBody = GenerateAdminEmailTemplate(delivery, trip, initiatedBy);

                    var adminResult = await SendEmailAsync(
                        client.PodEmail,
                        adminSubject,
                        adminBody,
                        podPdfBytes,
                        podAttachmentName,
                        "admin"
                    );
                    
                    if (!adminResult)
                    {
                        allEmailsSuccessful = false;
                    }
                }

                // 3. Send receipt to driver (if we have driver info)
                if (!string.IsNullOrEmpty(trip.SessionInfo.DriverId))
                {
                    var driver = await _firestoreService.GetDriverAsync(trip.SessionInfo.DriverId);
                    // Note: Driver model doesn't have email field yet, but we're future-proofing
                    // For now, we'll log this and potentially add driver email capability later
                    _logger.LogDebug("Driver receipt email not sent - driver email not configured");
                }

                _logger.LogInformation("POD emails process completed for delivery {DeliveryId}. All successful: {AllSuccessful}", 
                    deliveryId, allEmailsSuccessful);

                return allEmailsSuccessful;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending POD emails for delivery {DeliveryId}", deliveryId);
                return false;
            }
        }

        public async Task<bool> SendEmailAsync(string recipient, string subject, string body, 
            byte[]? attachmentData = null, string? attachmentName = null, string emailType = "general")
        {
            var emailLogId = Guid.NewGuid().ToString();
            
            try
            {
                // Validate email configuration
                if (string.IsNullOrEmpty(_emailConfig.SmtpHost) || string.IsNullOrEmpty(_emailConfig.SmtpUsername))
                {
                    _logger.LogWarning("Email configuration is incomplete");
                    return false;
                }

                // Create SMTP client
                using var smtpClient = new SmtpClient(_emailConfig.SmtpHost, _emailConfig.SmtpPort)
                {
                    Credentials = new NetworkCredential(_emailConfig.SmtpUsername, _emailConfig.SmtpPassword),
                    EnableSsl = _emailConfig.EnableSsl
                };

                // Create email message
                using var mailMessage = new MailMessage
                {
                    From = new MailAddress(_emailConfig.FromEmail, _emailConfig.FromName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = true
                };

                mailMessage.To.Add(recipient);

                // Add attachment if provided
                if (attachmentData != null && !string.IsNullOrEmpty(attachmentName))
                {
                    var attachment = new Attachment(new MemoryStream(attachmentData), attachmentName, "application/pdf");
                    mailMessage.Attachments.Add(attachment);
                }

                // Send email
                await smtpClient.SendMailAsync(mailMessage);

                _logger.LogInformation("Email sent successfully to {Recipient}", recipient);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {Recipient}", recipient);
                return false;
            }
        }


        private string GenerateCustomerEmailTemplate(Delivery delivery, Trip trip)
        {
            var template = new StringBuilder();
            
            template.AppendLine("<!DOCTYPE html>");
            template.AppendLine("<html><head><meta charset='UTF-8'><title>Delivery Confirmation</title></head>");
            template.AppendLine("<body style='font-family: Arial, sans-serif; margin: 20px; background: #f8f9fa;'>");
            
            template.AppendLine("<div style='background: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1);'>");
            
            // Header
            template.AppendLine("<div style='border-bottom: 3px solid #007bff; padding-bottom: 20px; margin-bottom: 30px; text-align: center;'>");
            template.AppendLine("<h1 style='color: #007bff; margin: 0; font-size: 28px;'>Delivery Confirmed</h1>");
            template.AppendLine("<p style='color: #6c757d; margin: 10px 0 0 0; font-size: 16px;'>Your order has been successfully delivered</p>");
            template.AppendLine("</div>");
            
            // Delivery Details
            template.AppendLine("<div style='margin-bottom: 30px;'>");
            template.AppendLine("<h2 style='color: #495057; font-size: 20px; margin-bottom: 15px;'>Delivery Details</h2>");
            template.AppendLine("<table style='width: 100%; border-collapse: collapse;'>");
            
            template.AppendLine($"<tr><td style='padding: 8px 0; font-weight: bold; color: #495057; width: 30%;'>Order Number:</td><td style='padding: 8px 0; color: #212529;'>{delivery.OrderNumber}</td></tr>");
            template.AppendLine($"<tr><td style='padding: 8px 0; font-weight: bold; color: #495057;'>Customer:</td><td style='padding: 8px 0; color: #212529;'>{delivery.Customer.Name}</td></tr>");
            template.AppendLine($"<tr><td style='padding: 8px 0; font-weight: bold; color: #495057;'>Address:</td><td style='padding: 8px 0; color: #212529;'>{delivery.Address}</td></tr>");
            template.AppendLine($"<tr><td style='padding: 8px 0; font-weight: bold; color: #495057;'>Quantity:</td><td style='padding: 8px 0; color: #212529;'>{delivery.QuantityExpected}</td></tr>");
            template.AppendLine($"<tr><td style='padding: 8px 0; font-weight: bold; color: #495057;'>Delivered:</td><td style='padding: 8px 0; color: #212529;'>{DateTime.UtcNow:dd MMMM yyyy, HH:mm}</td></tr>");
            
            if (!string.IsNullOrEmpty(trip.SessionInfo.VehicleId))
            {
                template.AppendLine($"<tr><td style='padding: 8px 0; font-weight: bold; color: #495057;'>Vehicle:</td><td style='padding: 8px 0; color: #212529;'>{trip.SessionInfo.VehicleId}</td></tr>");
            }
            
            template.AppendLine("</table>");
            template.AppendLine("</div>");
            
            // POD Notice
            template.AppendLine("<div style='background: #e7f3ff; border: 2px solid #007bff; border-radius: 6px; padding: 20px; margin-bottom: 30px;'>");
            template.AppendLine("<h3 style='color: #007bff; margin: 0 0 10px 0; font-size: 18px;'>📋 Proof of Delivery</h3>");
            template.AppendLine("<p style='margin: 0; color: #495057; line-height: 1.5;'>Your electronic Proof of Delivery (ePOD) is attached to this email. This document contains delivery confirmation details including signature and photo evidence where captured.</p>");
            template.AppendLine("</div>");
            
            // Footer
            template.AppendLine("<div style='border-top: 1px solid #dee2e6; padding-top: 20px; text-align: center; color: #6c757d;'>");
            template.AppendLine("<p style='margin: 0 0 10px 0; font-size: 14px;'>Thank you for your business!</p>");
            template.AppendLine("<p style='margin: 0; font-size: 12px;'>This is an automated message. Please do not reply to this email.</p>");
            template.AppendLine($"<p style='margin: 10px 0 0 0; font-size: 12px;'>Generated on {DateTime.UtcNow:dd MMMM yyyy} at {DateTime.UtcNow:HH:mm} UTC</p>");
            template.AppendLine("</div>");
            
            template.AppendLine("</div>");
            template.AppendLine("</body></html>");

            return template.ToString();
        }

        private string GenerateAdminEmailTemplate(Delivery delivery, Trip trip, string initiatedBy)
        {
            var template = new StringBuilder();
            
            template.AppendLine("<!DOCTYPE html>");
            template.AppendLine("<html><head><meta charset='UTF-8'><title>POD Generated</title></head>");
            template.AppendLine("<body style='font-family: Arial, sans-serif; margin: 20px; background: #f8f9fa;'>");
            
            template.AppendLine("<div style='background: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1);'>");
            
            // Header
            template.AppendLine("<div style='border-bottom: 3px solid #28a745; padding-bottom: 20px; margin-bottom: 30px;'>");
            template.AppendLine("<h1 style='color: #28a745; margin: 0; font-size: 24px;'>POD Generated</h1>");
            template.AppendLine("<p style='color: #6c757d; margin: 10px 0 0 0; font-size: 16px;'>Electronic Proof of Delivery has been created</p>");
            template.AppendLine("</div>");
            
            // Delivery Summary
            template.AppendLine("<div style='margin-bottom: 30px;'>");
            template.AppendLine("<h2 style='color: #495057; font-size: 18px; margin-bottom: 15px;'>Delivery Summary</h2>");
            template.AppendLine("<table style='width: 100%; border-collapse: collapse;'>");
            
            template.AppendLine($"<tr><td style='padding: 6px 0; font-weight: bold; color: #495057; width: 25%;'>Trip ID:</td><td style='padding: 6px 0; color: #212529;'>{trip.ClientId}</td></tr>");
            template.AppendLine($"<tr><td style='padding: 6px 0; font-weight: bold; color: #495057;'>Delivery ID:</td><td style='padding: 6px 0; color: #212529;'>{delivery.OrderNumber}</td></tr>");
            template.AppendLine($"<tr><td style='padding: 6px 0; font-weight: bold; color: #495057;'>Customer:</td><td style='padding: 6px 0; color: #212529;'>{delivery.Customer.Name}</td></tr>");
            template.AppendLine($"<tr><td style='padding: 6px 0; font-weight: bold; color: #495057;'>Address:</td><td style='padding: 6px 0; color: #212529;'>{delivery.Address}</td></tr>");
            template.AppendLine($"<tr><td style='padding: 6px 0; font-weight: bold; color: #495057;'>Driver:</td><td style='padding: 6px 0; color: #212529;'>{trip.SessionInfo.DriverId}</td></tr>");
            template.AppendLine($"<tr><td style='padding: 6px 0; font-weight: bold; color: #495057;'>Vehicle:</td><td style='padding: 6px 0; color: #212529;'>{trip.SessionInfo.VehicleId}</td></tr>");
            template.AppendLine($"<tr><td style='padding: 6px 0; font-weight: bold; color: #495057;'>Completed:</td><td style='padding: 6px 0; color: #212529;'>{DateTime.UtcNow:dd MMMM yyyy, HH:mm}</td></tr>");
            template.AppendLine($"<tr><td style='padding: 6px 0; font-weight: bold; color: #495057;'>Initiated By:</td><td style='padding: 6px 0; color: #212529;'>{initiatedBy}</td></tr>");
            
            template.AppendLine("</table>");
            template.AppendLine("</div>");
            
            // Footer
            template.AppendLine("<div style='border-top: 1px solid #dee2e6; padding-top: 20px; text-align: center; color: #6c757d;'>");
            template.AppendLine("<p style='margin: 0 0 10px 0; font-size: 14px;'>📎 POD document is attached to this email</p>");
            template.AppendLine("<p style='margin: 0; font-size: 12px;'>EasyDelivery System - Admin Notification</p>");
            template.AppendLine($"<p style='margin: 10px 0 0 0; font-size: 12px;'>Generated on {DateTime.UtcNow:dd MMMM yyyy} at {DateTime.UtcNow:HH:mm} UTC</p>");
            template.AppendLine("</div>");
            
            template.AppendLine("</div>");
            template.AppendLine("</body></html>");

            return template.ToString();
        }
    }
}