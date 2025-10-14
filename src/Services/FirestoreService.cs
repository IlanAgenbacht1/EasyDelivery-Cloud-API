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
        
        // Trip management operations
        Task<Dictionary<string, Trip>> GetTripsByClientAndStatusAsync(string clientId, string status);
        Task<Dictionary<string, Trip>> GetDriverTripsAsync(string driverId);
        Task ClaimTripAsync(string tripId, string driverId, string deviceId, string vehicleId, DateTime claimedAt);
        Task ReleaseTripAsync(string tripId);
        
        // Delivery operations (sub-collection under trips)
        Task SaveDeliveryAsync(string tripId, string deliveryId, Delivery delivery);
        Task<Delivery?> GetDeliveryAsync(string tripId, string deliveryId);
        Task UpdateDeliveryStatusAsync(string tripId, string deliveryId, string status, string? podData = null);
        
        // Delivery management operations
        Task<Dictionary<string, Delivery>> GetTripDeliveriesAsync(string tripId, string? status = null);
        
        // State management
        Task<bool> IsShipmentProcessedAsync(string clientId, decimal shiuniq);
        Task MarkShipmentProcessedAsync(ProcessedShipment processedShipment);
        Task<DateTime> GetLastProcessedDateAsync(string clientId);
        Task UpdateLastProcessedDateAsync(string clientId, DateTime date);
        
        // Authentication operations
        Task<Client?> ValidateEnrollmentKeyAsync(string enrollmentKey);
        Task<int> GetClientDeviceCountAsync(string clientId);
        Task<Device?> GetDeviceAsync(string deviceId);
        Task CreateDeviceAsync(string deviceId, Device device);
        Task UpdateDeviceLastSeenAsync(string deviceId, DateTime lastSeenAt);
        
        // Driver operations
        Task<Dictionary<string, Driver>> GetClientDriversAsync(string clientId);
        Task<Driver?> GetDriverAsync(string driverId);
        Task UpdateDriverPinAsync(string driverId, string pinHash);
        
        // Vehicle operations
        Task<Dictionary<string, Vehicle>> GetClientVehiclesAsync(string clientId);
        Task<Vehicle?> GetVehicleAsync(string vehicleId);
        
        // Session operations
        Task<KeyValuePair<string, Session>?> GetActiveDriverSessionAsync(string driverId);
        Task<bool> IsVehicleInUseAsync(string vehicleId);
        Task CreateSessionAsync(string sessionId, Session session);
        Task<Session?> GetSessionAsync(string sessionId);
        Task EndSessionAsync(string sessionId);
        Task UpdateSessionLastActiveAsync(string sessionId, DateTime lastActiveAt);
        
        // File metadata operations
        Task SaveFileMetadataAsync(string fileId, FileMetadata fileMetadata);
        Task<FileMetadata?> GetFileMetadataAsync(string fileId);
        Task DeactivateFileAsync(string fileId);
        
        // POD document operations
        Task SavePodDocumentAsync(string podDocumentId, PodDocument podDocument);
        Task<PodDocument?> GetPodDocumentAsync(string podDocumentId);
        
        // Email configuration and logging operations
        Task<ClientEmailConfig?> GetClientEmailConfigAsync(string clientId);
        Task SaveEmailLogAsync(string emailLogId, EmailLog emailLog);
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

        // Authentication operations implementation
        public async Task<Client?> ValidateEnrollmentKeyAsync(string enrollmentKey)
        {
            var query = _firestoreDb.Collection("clients")
                .WhereEqualTo("enrollmentKey", enrollmentKey)
                .WhereEqualTo("status", "active");

            var snapshot = await query.GetSnapshotAsync();
            var client = snapshot.Documents.FirstOrDefault();

            if (client != null && client.Exists)
            {
                var clientData = client.ConvertTo<Client>();
                // Set the clientId from document ID
                clientData.ClientId = client.Id;
                
                // Check if key is still valid
                if (clientData.KeyExpiresAt > DateTime.UtcNow)
                {
                    return clientData;
                }
            }

            return null;
        }

        public async Task<int> GetClientDeviceCountAsync(string clientId)
        {
            var query = _firestoreDb.Collection("devices")
                .WhereEqualTo("clientId", clientId);

            var snapshot = await query.GetSnapshotAsync();
            return snapshot.Count;
        }

        public async Task<Device?> GetDeviceAsync(string deviceId)
        {
            var docRef = _firestoreDb.Collection("devices").Document(deviceId);
            var snapshot = await docRef.GetSnapshotAsync();

            if (snapshot.Exists)
            {
                return snapshot.ConvertTo<Device>();
            }

            return null;
        }

        public async Task CreateDeviceAsync(string deviceId, Device device)
        {
            var docRef = _firestoreDb.Collection("devices").Document(deviceId);
            await docRef.SetAsync(device);
            _logger.LogDebug("Created device {DeviceId} for client {ClientId}", deviceId, device.ClientId);
        }

        public async Task UpdateDeviceLastSeenAsync(string deviceId, DateTime lastSeenAt)
        {
            var docRef = _firestoreDb.Collection("devices").Document(deviceId);
            await docRef.UpdateAsync(new Dictionary<string, object>
            {
                { "lastSeenAt", lastSeenAt }
            });
        }

        // Driver operations implementation
        public async Task<Dictionary<string, Driver>> GetClientDriversAsync(string clientId)
        {
            var query = _firestoreDb.Collection("drivers")
                .WhereEqualTo("clientId", clientId);

            var snapshot = await query.GetSnapshotAsync();
            var drivers = new Dictionary<string, Driver>();

            foreach (var document in snapshot.Documents)
            {
                if (document.Exists)
                {
                    drivers[document.Id] = document.ConvertTo<Driver>();
                }
            }

            return drivers;
        }

        public async Task<Driver?> GetDriverAsync(string driverId)
        {
            var docRef = _firestoreDb.Collection("drivers").Document(driverId);
            var snapshot = await docRef.GetSnapshotAsync();

            if (snapshot.Exists)
            {
                return snapshot.ConvertTo<Driver>();
            }

            return null;
        }

        public async Task UpdateDriverPinAsync(string driverId, string pinHash)
        {
            var docRef = _firestoreDb.Collection("drivers").Document(driverId);
            await docRef.UpdateAsync(new Dictionary<string, object>
            {
                { "pinHash", pinHash }
            });
            _logger.LogDebug("Updated PIN hash for driver {DriverId}", driverId);
        }

        // Vehicle operations implementation
        public async Task<Dictionary<string, Vehicle>> GetClientVehiclesAsync(string clientId)
        {
            var query = _firestoreDb.Collection("vehicles")
                .WhereEqualTo("clientId", clientId);

            var snapshot = await query.GetSnapshotAsync();
            var vehicles = new Dictionary<string, Vehicle>();

            foreach (var document in snapshot.Documents)
            {
                if (document.Exists)
                {
                    vehicles[document.Id] = document.ConvertTo<Vehicle>();
                }
            }

            return vehicles;
        }

        public async Task<Vehicle?> GetVehicleAsync(string vehicleId)
        {
            var docRef = _firestoreDb.Collection("vehicles").Document(vehicleId);
            var snapshot = await docRef.GetSnapshotAsync();

            if (snapshot.Exists)
            {
                return snapshot.ConvertTo<Vehicle>();
            }

            return null;
        }

        // Session operations implementation
        public async Task<KeyValuePair<string, Session>?> GetActiveDriverSessionAsync(string driverId)
        {
            var query = _firestoreDb.Collection("sessions")
                .WhereEqualTo("driverId", driverId)
                .WhereEqualTo("isActive", true);

            var snapshot = await query.GetSnapshotAsync();
            var activeSession = snapshot.Documents.FirstOrDefault();

            if (activeSession != null && activeSession.Exists)
            {
                return new KeyValuePair<string, Session>(activeSession.Id, activeSession.ConvertTo<Session>());
            }

            return null;
        }

        public async Task<bool> IsVehicleInUseAsync(string vehicleId)
        {
            var query = _firestoreDb.Collection("sessions")
                .WhereEqualTo("vehicleId", vehicleId)
                .WhereEqualTo("isActive", true);

            var snapshot = await query.GetSnapshotAsync();
            return snapshot.Count > 0;
        }

        public async Task CreateSessionAsync(string sessionId, Session session)
        {
            var docRef = _firestoreDb.Collection("sessions").Document(sessionId);
            await docRef.SetAsync(session);
            _logger.LogDebug("Created session {SessionId} for driver {DriverId}", sessionId, session.DriverId);
        }

        public async Task<Session?> GetSessionAsync(string sessionId)
        {
            var docRef = _firestoreDb.Collection("sessions").Document(sessionId);
            var snapshot = await docRef.GetSnapshotAsync();

            if (snapshot.Exists)
            {
                return snapshot.ConvertTo<Session>();
            }

            return null;
        }

        public async Task EndSessionAsync(string sessionId)
        {
            var docRef = _firestoreDb.Collection("sessions").Document(sessionId);
            await docRef.UpdateAsync(new Dictionary<string, object>
            {
                { "isActive", false },
                { "lastActiveAt", DateTime.UtcNow }
            });
            _logger.LogDebug("Ended session {SessionId}", sessionId);
        }

        public async Task UpdateSessionLastActiveAsync(string sessionId, DateTime lastActiveAt)
        {
            var docRef = _firestoreDb.Collection("sessions").Document(sessionId);
            await docRef.UpdateAsync(new Dictionary<string, object>
            {
                { "lastActiveAt", lastActiveAt }
            });
        }

        // Trip management operations implementation
        public async Task<Dictionary<string, Trip>> GetTripsByClientAndStatusAsync(string clientId, string status)
        {
            var query = _firestoreDb.Collection("trips")
                .WhereEqualTo("clientId", clientId)
                .WhereEqualTo("status", status)
                .OrderBy("createdAt");

            var snapshot = await query.GetSnapshotAsync();
            var trips = new Dictionary<string, Trip>();

            foreach (var document in snapshot.Documents)
            {
                if (document.Exists)
                {
                    trips[document.Id] = document.ConvertTo<Trip>();
                }
            }

            _logger.LogDebug("Found {Count} trips for client {ClientId} with status {Status}", 
                trips.Count, clientId, status);
            return trips;
        }

        public async Task<Dictionary<string, Trip>> GetDriverTripsAsync(string driverId)
        {
            var query = _firestoreDb.Collection("trips")
                .WhereEqualTo("sessionInfo.driverId", driverId)
                .WhereIn("status", new[] { "claimed", "in_progress", "completed" })
                .OrderBy("claimedAt");

            var snapshot = await query.GetSnapshotAsync();
            var trips = new Dictionary<string, Trip>();

            foreach (var document in snapshot.Documents)
            {
                if (document.Exists)
                {
                    trips[document.Id] = document.ConvertTo<Trip>();
                }
            }

            _logger.LogDebug("Found {Count} trips for driver {DriverId}", trips.Count, driverId);
            return trips;
        }

        public async Task ClaimTripAsync(string tripId, string driverId, string deviceId, string vehicleId, DateTime claimedAt)
        {
            var docRef = _firestoreDb.Collection("trips").Document(tripId);
            
            var updates = new Dictionary<string, object>
            {
                { "status", "claimed" },
                { "claimedAt", claimedAt },
                { "sessionInfo.driverId", driverId },
                { "sessionInfo.deviceId", deviceId },
                { "sessionInfo.vehicleId", vehicleId }
            };
            
            await docRef.UpdateAsync(updates);
            _logger.LogDebug("Trip {TripId} claimed by driver {DriverId} with vehicle {VehicleId}", 
                tripId, driverId, vehicleId);
        }

        public async Task ReleaseTripAsync(string tripId)
        {
            var docRef = _firestoreDb.Collection("trips").Document(tripId);
            
            var updates = new Dictionary<string, object>
            {
                { "status", "available" },
                { "claimedAt", FieldValue.Delete },
                { "sessionInfo.driverId", "" },
                { "sessionInfo.deviceId", "" },
                { "sessionInfo.vehicleId", "" }
            };
            
            await docRef.UpdateAsync(updates);
            _logger.LogDebug("Trip {TripId} released back to available pool", tripId);
        }

        // Delivery management operations implementation
        public async Task<Dictionary<string, Delivery>> GetTripDeliveriesAsync(string tripId, string? status = null)
        {
            var collectionRef = _firestoreDb.Collection("trips").Document(tripId).Collection("deliveries");
            
            Query query = collectionRef;
            
            // Filter by status if provided
            if (!string.IsNullOrEmpty(status))
            {
                query = query.WhereEqualTo("status", status);
            }
            
            var snapshot = await query.GetSnapshotAsync();
            var deliveries = new Dictionary<string, Delivery>();

            foreach (var document in snapshot.Documents)
            {
                if (document.Exists)
                {
                    deliveries[document.Id] = document.ConvertTo<Delivery>();
                }
            }

            _logger.LogDebug("Found {Count} deliveries for trip {TripId} with status filter '{Status}'", 
                deliveries.Count, tripId, status ?? "all");
            return deliveries;
        }

        // File metadata operations implementation
        public async Task SaveFileMetadataAsync(string fileId, FileMetadata fileMetadata)
        {
            var docRef = _firestoreDb.Collection("file-metadata").Document(fileId);
            await docRef.SetAsync(fileMetadata);
            _logger.LogDebug("Saved file metadata {FileId} for client {ClientId}", fileId, fileMetadata.ClientId);
        }

        public async Task<FileMetadata?> GetFileMetadataAsync(string fileId)
        {
            var docRef = _firestoreDb.Collection("file-metadata").Document(fileId);
            var snapshot = await docRef.GetSnapshotAsync();

            if (snapshot.Exists)
            {
                return snapshot.ConvertTo<FileMetadata>();
            }

            return null;
        }

        public async Task DeactivateFileAsync(string fileId)
        {
            var docRef = _firestoreDb.Collection("file-metadata").Document(fileId);
            await docRef.UpdateAsync(new Dictionary<string, object>
            {
                { "isActive", false }
            });
            _logger.LogDebug("Deactivated file {FileId}", fileId);
        }

        // POD document operations implementation
        public async Task SavePodDocumentAsync(string podDocumentId, PodDocument podDocument)
        {
            var docRef = _firestoreDb.Collection("pod-documents").Document(podDocumentId);
            await docRef.SetAsync(podDocument);
            _logger.LogDebug("Saved POD document {PodDocumentId} for delivery {DeliveryId}", 
                podDocumentId, podDocument.DeliveryId);
        }

        public async Task<PodDocument?> GetPodDocumentAsync(string podDocumentId)
        {
            var docRef = _firestoreDb.Collection("pod-documents").Document(podDocumentId);
            var snapshot = await docRef.GetSnapshotAsync();

            if (snapshot.Exists)
            {
                return snapshot.ConvertTo<PodDocument>();
            }

            return null;
        }
        
        // Email configuration and logging operations implementation
        public async Task<ClientEmailConfig?> GetClientEmailConfigAsync(string clientId)
        {
            var docRef = _firestoreDb.Collection("client-email-configs").Document(clientId);
            var snapshot = await docRef.GetSnapshotAsync();

            if (snapshot.Exists)
            {
                return snapshot.ConvertTo<ClientEmailConfig>();
            }

            _logger.LogDebug("No email configuration found for client {ClientId}", clientId);
            return null;
        }
        
        public async Task SaveEmailLogAsync(string emailLogId, EmailLog emailLog)
        {
            var docRef = _firestoreDb.Collection("email-logs").Document(emailLogId);
            await docRef.SetAsync(emailLog);
            _logger.LogDebug("Saved email log {EmailLogId} for client {ClientId}", 
                emailLogId, emailLog.ClientId);
        }
    }
}
