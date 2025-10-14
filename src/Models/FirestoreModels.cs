using System;
using System.Collections.Generic;
using Google.Cloud.Firestore;

namespace DeliveryCloudApi.Models
{
    /// <summary>
    /// Client collection - matches clients.json schema exactly
    /// Document ID: clientId (e.g., "dev")
    /// Integration service uses this to get active clients and their Sage 300 connection details
    /// </summary>
    [FirestoreData]
    public class Client
    {
        // Document ID is used as ClientId (not stored as Firestore property)
        public string ClientId { get; set; } = string.Empty;
        
        [FirestoreProperty("clientName")]
        public string ClientName { get; set; } = string.Empty;

        [FirestoreProperty("createdAt")]
        public DateTime CreatedAt { get; set; }

        [FirestoreProperty("deviceLimit")]
        public int DeviceLimit { get; set; }

        [FirestoreProperty("enrollmentKey")]
        public string EnrollmentKey { get; set; } = string.Empty;

        [FirestoreProperty("integrationProfile")]
        public IntegrationProfile IntegrationProfile { get; set; } = new();

        [FirestoreProperty("keyExpiresAt")]
        public DateTime KeyExpiresAt { get; set; }

        [FirestoreProperty("podEmail")]
        public string PodEmail { get; set; } = string.Empty;

        [FirestoreProperty("status")]
        public string Status { get; set; } = "active"; // "active||disabled"
    }

    [FirestoreData]
    public class IntegrationProfile
    {
        [FirestoreProperty("type")]
        public string Type { get; set; } = "SAGE_300";

        [FirestoreProperty("credentials")]
        public Dictionary<string, object> Credentials { get; set; } = new();
        // Will include: connectionString, pollingConfig, etc.
    }

    /// <summary>
    /// Trip collection - matches trips.json schema
    /// Document ID: Trip number (ROUTE field or order number, e.g., "DEV000001")
    /// Integration service creates these from Sage 300 shipments
    /// </summary>
    [FirestoreData]
    public class Trip
    {
        [FirestoreProperty("claimedAt")]
        public DateTime? ClaimedAt { get; set; }

        [FirestoreProperty("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [FirestoreProperty("completedAt")]
        public DateTime? CompletedAt { get; set; }

        [FirestoreProperty("createdAt")]
        public DateTime CreatedAt { get; set; }

        [FirestoreProperty("sessionInfo")]
        public SessionInfo SessionInfo { get; set; } = new();

        [FirestoreProperty("status")]
        public string Status { get; set; } = "available"; // "available||claimed||in_progress||completed"
    }

    [FirestoreData]
    public class SessionInfo
    {
        [FirestoreProperty("vehicleId")]
        public string VehicleId { get; set; } = string.Empty;

        [FirestoreProperty("deviceId")]
        public string DeviceId { get; set; } = string.Empty;

        [FirestoreProperty("driverId")]
        public string DriverId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Delivery sub-collection under trips - matches deliveries.json schema
    /// Collection path: trips/{tripId}/deliveries/{deliveryId}
    /// Document ID: SHINUMBER (e.g., "DEVSH0001")
    /// Integration service creates these from Sage 300 shipment details
    /// </summary>
    [FirestoreData]
    public class Delivery
    {
        [FirestoreProperty("address")]
        public string Address { get; set; } = string.Empty;

        [FirestoreProperty("customer")]
        public Customer Customer { get; set; } = new();

        [FirestoreProperty("gpsLocation")]
        public string GpsLocation { get; set; } = string.Empty; // "[34.090194° S, 18.843545° E]"

        [FirestoreProperty("orderNumber")]
        public string OrderNumber { get; set; } = string.Empty;

        [FirestoreProperty("quantityExpected")]
        public string QuantityExpected { get; set; } = string.Empty;

        [FirestoreProperty("status")]
        public string Status { get; set; } = "pending"; // "pending||completed"

        // POD data - integration service creates with empty template, mobile app fills it
        [FirestoreProperty("podData")]
        public string PodData { get; set; } = "{\"signatureUrl\": \"\", \"capturedGpsLocation\": \"[0° N, 0° E]\", \"photoUrl\": \"\", \"comment\": \"\", \"recipientName\": \"\", \"quantity\": \"\"}"; 
    }

    [FirestoreData]
    public class Customer
    {
        [FirestoreProperty("number")]
        public string Number { get; set; } = string.Empty; // SHPPHONE or BILPHONE

        [FirestoreProperty("contact")]
        public string Contact { get; set; } = string.Empty; // SHPCONTACT or BILCONTACT

        [FirestoreProperty("name")]
        public string Name { get; set; } = string.Empty; // Cleaned SHPNAME or BILNAME

        [FirestoreProperty("email")]
        public string Email { get; set; } = string.Empty; // BILEMAIL from Sage
    }

    /// <summary>
    /// Driver collection - for mobile app authentication
    /// Document ID: driverId (e.g., "driver1", "driver2")
    /// </summary>
    [FirestoreData]
    public class Driver
    {
        [FirestoreProperty("name")]
        public string Name { get; set; } = string.Empty;

        [FirestoreProperty("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [FirestoreProperty("pinHash")]
        public string PinHash { get; set; } = string.Empty; // Empty until PIN is set up

        [FirestoreProperty("isActive")]
        public bool IsActive { get; set; } = true;

        [FirestoreProperty("createdAt")]
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Device collection - for device registration and approval
    /// Document ID: deviceId (device UID)
    /// </summary>
    [FirestoreData]
    public class Device
    {
        [FirestoreProperty("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [FirestoreProperty("deviceType")]
        public string DeviceType { get; set; } = string.Empty; // "tablet", "phone", etc.

        [FirestoreProperty("isApproved")]
        public bool IsApproved { get; set; } = false;

        [FirestoreProperty("lastSeenAt")]
        public DateTime LastSeenAt { get; set; }

        [FirestoreProperty("registeredAt")]
        public DateTime RegisteredAt { get; set; }
    }

    /// <summary>
    /// Vehicle collection - vehicle registration info
    /// Document ID: vehicleId (registration number)
    /// </summary>
    [FirestoreData]
    public class Vehicle
    {
        [FirestoreProperty("registrationNumber")]
        public string RegistrationNumber { get; set; } = string.Empty;

        [FirestoreProperty("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [FirestoreProperty("createdAt")]
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Session collection - active driver sessions
    /// Document ID: sessionId (unique session identifier)
    /// </summary>
    [FirestoreData]
    public class Session
    {
        [FirestoreProperty("driverId")]
        public string DriverId { get; set; } = string.Empty;

        [FirestoreProperty("deviceId")]
        public string DeviceId { get; set; } = string.Empty;

        [FirestoreProperty("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [FirestoreProperty("vehicleId")]
        public string VehicleId { get; set; } = string.Empty;

        [FirestoreProperty("createdAt")]
        public DateTime CreatedAt { get; set; }

        [FirestoreProperty("lastActiveAt")]
        public DateTime LastActiveAt { get; set; }

        [FirestoreProperty("isActive")]
        public bool IsActive { get; set; } = true;
    }

    /// <summary>
    /// For tracking processed shipments from Sage 300 (internal state management)
    /// Collection: processed-shipments
    /// Document ID: clientId-SHIUNIQ (e.g., "dev-12345")
    /// </summary>
    [FirestoreData]
    public class ProcessedShipment
    {
        [FirestoreProperty("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [FirestoreProperty("shiuniq")]
        public decimal SHIUNIQ { get; set; }

        [FirestoreProperty("ordnumber")]
        public string ORDNUMBER { get; set; } = string.Empty;

        [FirestoreProperty("shinumber")]
        public string SHINUMBER { get; set; } = string.Empty;

        [FirestoreProperty("shipmentDate")]
        public DateTime ShipmentDate { get; set; }

        [FirestoreProperty("processedDate")]
        public DateTime ProcessedDate { get; set; }
    }

    /// <summary>
    /// FileMetadata collection - for tracking uploaded files
    /// Document ID: fileId (unique file identifier)
    /// </summary>
    [FirestoreData]
    public class FileMetadata
    {
        [FirestoreProperty("fileName")]
        public string FileName { get; set; } = string.Empty;

        [FirestoreProperty("originalFileName")]
        public string OriginalFileName { get; set; } = string.Empty;

        [FirestoreProperty("contentType")]
        public string ContentType { get; set; } = string.Empty;

        [FirestoreProperty("fileSize")]
        public long FileSize { get; set; }

        [FirestoreProperty("fileType")]
        public string FileType { get; set; } = string.Empty; // "signature", "photo", "document"

        [FirestoreProperty("uploadedBy")]
        public string UploadedBy { get; set; } = string.Empty; // DriverId

        [FirestoreProperty("uploadedFrom")]
        public string UploadedFrom { get; set; } = string.Empty; // DeviceId

        [FirestoreProperty("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [FirestoreProperty("tripId")]
        public string TripId { get; set; } = string.Empty;

        [FirestoreProperty("deliveryId")]
        public string DeliveryId { get; set; } = string.Empty;

        [FirestoreProperty("uploadedAt")]
        public DateTime UploadedAt { get; set; }

        [FirestoreProperty("isActive")]
        public bool IsActive { get; set; } = true;

        [FirestoreProperty("filePath")]
        public string FilePath { get; set; } = string.Empty; // Storage path

        [FirestoreProperty("publicUrl")]
        public string PublicUrl { get; set; } = string.Empty; // Public access URL if needed
    }

    /// <summary>
    /// POD Document collection - for tracking generated POD PDFs
    /// Document ID: podDocumentId (unique POD document identifier)
    /// </summary>
    [FirestoreData]
    public class PodDocument
    {
        [FirestoreProperty("deliveryId")]
        public string DeliveryId { get; set; } = string.Empty;

        [FirestoreProperty("tripId")]
        public string TripId { get; set; } = string.Empty;

        [FirestoreProperty("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [FirestoreProperty("fileName")]
        public string FileName { get; set; } = string.Empty;

        [FirestoreProperty("filePath")]
        public string FilePath { get; set; } = string.Empty;

        [FirestoreProperty("fileSize")]
        public long FileSize { get; set; }

        [FirestoreProperty("generatedAt")]
        public DateTime GeneratedAt { get; set; }

        [FirestoreProperty("generatedBy")]
        public string GeneratedBy { get; set; } = string.Empty; // DriverId or "system"

        [FirestoreProperty("hasSignature")]
        public bool HasSignature { get; set; }

        [FirestoreProperty("hasPhoto")]
        public bool HasPhoto { get; set; }

        [FirestoreProperty("publicUrl")]
        public string PublicUrl { get; set; } = string.Empty;

        [FirestoreProperty("isActive")]
        public bool IsActive { get; set; } = true;
    }

    /// <summary>
    /// Client Email Configuration - per-client email settings
    /// Document ID: clientId
    /// </summary>
    [FirestoreData]
    public class ClientEmailConfig
    {
        [FirestoreProperty("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [FirestoreProperty("smtpHost")]
        public string SmtpHost { get; set; } = string.Empty;

        [FirestoreProperty("smtpPort")]
        public int SmtpPort { get; set; } = 587;

        [FirestoreProperty("smtpUsername")]
        public string SmtpUsername { get; set; } = string.Empty;

        [FirestoreProperty("smtpPassword")]
        public string SmtpPassword { get; set; } = string.Empty; // Should be encrypted

        [FirestoreProperty("fromEmail")]
        public string FromEmail { get; set; } = string.Empty;

        [FirestoreProperty("fromName")]
        public string FromName { get; set; } = string.Empty;

        [FirestoreProperty("adminEmails")]
        public List<string> AdminEmails { get; set; } = new(); // Copy recipients

        [FirestoreProperty("enableSsl")]
        public bool EnableSsl { get; set; } = true;

        [FirestoreProperty("isActive")]
        public bool IsActive { get; set; } = true;

        [FirestoreProperty("createdAt")]
        public DateTime CreatedAt { get; set; }

        [FirestoreProperty("updatedAt")]
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// Email Log collection - track sent emails
    /// Document ID: emailLogId (unique identifier)
    /// </summary>
    [FirestoreData]
    public class EmailLog
    {
        [FirestoreProperty("deliveryId")]
        public string DeliveryId { get; set; } = string.Empty;

        [FirestoreProperty("tripId")]
        public string TripId { get; set; } = string.Empty;

        [FirestoreProperty("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [FirestoreProperty("emailType")]
        public string EmailType { get; set; } = string.Empty; // "customer", "admin", "driver"

        [FirestoreProperty("recipient")]
        public string Recipient { get; set; } = string.Empty;

        [FirestoreProperty("subject")]
        public string Subject { get; set; } = string.Empty;

        [FirestoreProperty("status")]
        public string Status { get; set; } = string.Empty; // "queued", "sent", "failed"

        [FirestoreProperty("sentAt")]
        public DateTime? SentAt { get; set; }

        [FirestoreProperty("errorMessage")]
        public string ErrorMessage { get; set; } = string.Empty;

        [FirestoreProperty("retryCount")]
        public int RetryCount { get; set; } = 0;

        [FirestoreProperty("createdAt")]
        public DateTime CreatedAt { get; set; }

        [FirestoreProperty("hasPodAttachment")]
        public bool HasPodAttachment { get; set; }

        [FirestoreProperty("initiatedBy")]
        public string InitiatedBy { get; set; } = string.Empty; // DriverId or "system"
    }

    /// <summary>
    /// Polling state per client (internal state management)
    /// Collection: polling-state
    /// Document ID: clientId
    /// </summary>
    [FirestoreData]
    public class PollingState
    {
        [FirestoreProperty("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [FirestoreProperty("lastProcessedDate")]
        public DateTime LastProcessedDate { get; set; }

        [FirestoreProperty("updatedDate")]
        public DateTime UpdatedDate { get; set; }
    }
}
