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
