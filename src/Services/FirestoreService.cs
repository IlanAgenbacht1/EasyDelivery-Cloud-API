using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging;
using DeliveryCloudApi.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DeliveryCloudApi.Services
{
    public interface IFirestoreService
    {
        // Client operations
        Task<List<Client>> GetActiveClientsAsync();
        Task<Client?> GetClientAsync(string clientId);
        
        // Trip operations
        Task<Trip?> GetTripAsync(string tripId);
        Task SaveTripAsync(string tripId, Trip trip);
        Task<bool> TripExistsAsync(string tripId);
        Task UpdateTripStatusAsync(string tripId, string status, DateTime? claimedAt = null, DateTime? completedAt = null);
        
        // Delivery operations (sub-collection under trips)
        Task SaveDeliveryAsync(string tripId, string deliveryId, Delivery delivery);
        Task<Delivery?> GetDeliveryAsync(string tripId, string deliveryId);
        Task UpdateDeliveryStatusAsync(string tripId, string deliveryId, string status, string? podData = null);
        
        // State management
        Task<bool> IsShipmentProcessedAsync(string clientId, decimal shiuniq);
        Task MarkShipmentProcessedAsync(ProcessedShipment processedShipment);
        Task<DateTime> GetLastProcessedDateAsync(string clientId);
        Task UpdateLastProcessedDateAsync(string clientId, DateTime date);
    }

    public class FirestoreService : IFirestoreService
    {
        private readonly FirestoreDb _firestoreDb;
        private readonly ILogger<FirestoreService> _logger;

        public FirestoreService(FirestoreDb firestoreDb, ILogger<FirestoreService> logger)
        {
            _firestoreDb = firestoreDb;
            _logger = logger;
        }

        public async Task<List<Client>> GetActiveClientsAsync()
        {
            _logger.LogDebug("Getting active clients from Firestore");

            var query = _firestoreDb.Collection("clients")
                .WhereEqualTo("status", "active");

            var snapshot = await query.GetSnapshotAsync();
            var clients = new List<Client>();

            foreach (var document in snapshot.Documents)
            {
                if (document.Exists)
                {
                    var client = document.ConvertTo<Client>();
                    clients.Add(client);
                }
            }

            _logger.LogInformation("Found {Count} active clients", clients.Count);
            return clients;
        }

        public async Task<Client?> GetClientAsync(string clientId)
        {
            var docRef = _firestoreDb.Collection("clients").Document(clientId);
            var snapshot = await docRef.GetSnapshotAsync();

            if (snapshot.Exists)
            {
                return snapshot.ConvertTo<Client>();
            }

            return null;
        }

        public async Task<Trip?> GetTripAsync(string tripId)
        {
            var docRef = _firestoreDb.Collection("trips").Document(tripId);
            var snapshot = await docRef.GetSnapshotAsync();
            
            if (snapshot.Exists)
            {
                return snapshot.ConvertTo<Trip>();
            }
            
            return null;
        }

        public async Task SaveTripAsync(string tripId, Trip trip)
        {
            var docRef = _firestoreDb.Collection("trips").Document(tripId);
            await docRef.SetAsync(trip);
            _logger.LogDebug("Saved trip {TripId} for client {ClientId}", tripId, trip.ClientId);
        }

        public async Task<bool> TripExistsAsync(string tripId)
        {
            var docRef = _firestoreDb.Collection("trips").Document(tripId);
            var snapshot = await docRef.GetSnapshotAsync();
            return snapshot.Exists;
        }

        public async Task UpdateTripStatusAsync(string tripId, string status, DateTime? claimedAt = null, DateTime? completedAt = null)
        {
            var docRef = _firestoreDb.Collection("trips").Document(tripId);
            
            var updates = new Dictionary<string, object>
            {
                { "status", status }
            };
            
            if (claimedAt.HasValue)
            {
                updates["claimedAt"] = claimedAt.Value;
            }
            
            if (completedAt.HasValue)
            {
                updates["completedAt"] = completedAt.Value;
            }
            
            // Special handling for reopening completed trips
            if (status == "available" && completedAt == null)
            {
                // Clear completion timestamp when reopening
                updates["completedAt"] = FieldValue.Delete;
            }
            
            await docRef.UpdateAsync(updates);
            _logger.LogDebug("Updated trip {TripId} status to {Status}", tripId, status);
        }

        public async Task SaveDeliveryAsync(string tripId, string deliveryId, Delivery delivery)
        {
            var docRef = _firestoreDb.Collection("trips").Document(tripId)
                .Collection("deliveries").Document(deliveryId);
            await docRef.SetAsync(delivery);
            _logger.LogDebug("Saved delivery {DeliveryId} under trip {TripId}", deliveryId, tripId);
        }

        public async Task<Delivery?> GetDeliveryAsync(string tripId, string deliveryId)
        {
            var docRef = _firestoreDb.Collection("trips").Document(tripId)
                .Collection("deliveries").Document(deliveryId);
            var snapshot = await docRef.GetSnapshotAsync();
            
            if (snapshot.Exists)
            {
                return snapshot.ConvertTo<Delivery>();
            }
            
            return null;
        }

        public async Task UpdateDeliveryStatusAsync(string tripId, string deliveryId, string status, string? podData = null)
        {
            var docRef = _firestoreDb.Collection("trips").Document(tripId)
                .Collection("deliveries").Document(deliveryId);
            
            var updates = new Dictionary<string, object>
            {
                { "status", status }
            };
            
            if (!string.IsNullOrWhiteSpace(podData))
            {
                updates["podData"] = podData;
            }
            
            await docRef.UpdateAsync(updates);
            _logger.LogDebug("Updated delivery {DeliveryId} status to {Status} under trip {TripId}", 
                deliveryId, status, tripId);
        }

        public async Task<bool> IsShipmentProcessedAsync(string clientId, decimal shiuniq)
        {
            var docId = $"{clientId}-{shiuniq}";
            var docRef = _firestoreDb.Collection("processed-shipments").Document(docId);
            var snapshot = await docRef.GetSnapshotAsync();
            return snapshot.Exists;
        }

        public async Task MarkShipmentProcessedAsync(ProcessedShipment processedShipment)
        {
            var docId = $"{processedShipment.ClientId}-{processedShipment.SHIUNIQ}";
            var docRef = _firestoreDb.Collection("processed-shipments").Document(docId);
            await docRef.SetAsync(processedShipment);
        }

        public async Task<DateTime> GetLastProcessedDateAsync(string clientId)
        {
            var docRef = _firestoreDb.Collection("polling-state").Document(clientId);
            var snapshot = await docRef.GetSnapshotAsync();
            
            if (snapshot.Exists)
            {
                var pollingState = snapshot.ConvertTo<PollingState>();
                return pollingState.LastProcessedDate;
            }
            
            // Return default date if no state exists
            return DateTime.Today.AddDays(-30);
        }

        public async Task UpdateLastProcessedDateAsync(string clientId, DateTime date)
        {
            var docRef = _firestoreDb.Collection("polling-state").Document(clientId);
            var pollingState = new PollingState
            {
                ClientId = clientId,
                LastProcessedDate = date,
                UpdatedDate = DateTime.UtcNow
            };
            await docRef.SetAsync(pollingState);
        }
    }
}
