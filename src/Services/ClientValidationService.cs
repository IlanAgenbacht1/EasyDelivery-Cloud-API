using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DeliveryCloudApi.Models;

namespace DeliveryCloudApi.Services
{
    public interface IClientValidationService
    {
        Task<Client?> ValidateClientAsync(string clientId);
    }

    public class ClientValidationService : IClientValidationService
    {
        private readonly IFirestoreService _firestoreService;
        private readonly ILogger<ClientValidationService> _logger;

        public ClientValidationService(
            IFirestoreService firestoreService,
            ILogger<ClientValidationService> logger)
        {
            _firestoreService = firestoreService;
            _logger = logger;
        }

        public async Task<Client?> ValidateClientAsync(string clientId)
        {
            if (string.IsNullOrEmpty(clientId))
            {
                _logger.LogWarning("Client validation failed: missing clientId");
                return null;
            }

            try
            {
                // Get client from Firestore
                var client = await _firestoreService.GetClientAsync(clientId);
                if (client == null)
                {
                    _logger.LogWarning("Client not found: {ClientId}", clientId);
                    return null;
                }

                // Check if client is active
                if (client.Status != "active")
                {
                    _logger.LogWarning("Client is not active: {ClientId}, Status: {Status}", clientId, client.Status);
                    return null;
                }

                // Authentication is handled by Google Cloud at the infrastructure level
                // We only need to validate that the client exists and is active
                _logger.LogInformation("Client validation successful: {ClientId}", clientId);
                return client;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating client: {ClientId}", clientId);
                return null;
            }
        }
    }
}