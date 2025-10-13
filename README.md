# Delivery Cloud Functions - South Africa

Multi-tenant Sage 300 delivery management system deployed to Google Cloud Run in **africa-south1** (South Africa).

Multi-tenant delivery management system using Google Cloud Functions and Firestore.

## Project Structure

```
G:\Work\Functions\DeliveryCloudFunctions\
├── src\
│   ├── Functions\                   # Cloud Functions
│   │   └── PollSageFunction.cs      # Polls Sage 300 for all clients
│   ├── Models\
│   │   ├── FirestoreModels.cs       # New Firestore schema models
│   │   └── SageModels.cs           # Existing Sage 300 models
│   ├── Services\
│   │   └── FirestoreService.cs      # Firestore operations
│   ├── Configuration\               # Configuration classes
│   └── Program.cs                   # Function startup
├── appsettings.json                # Production configuration
├── local.settings.json            # Development configuration
├── host.json                      # Function host configuration
└── DeliveryCloudFunctions.csproj  # Project file
```

## Architecture

### Multi-Tenant Structure
- **clients**: Client information with integration profiles
- **devices**: Mobile devices (Firebase Auth UIDs)
- **drivers**: Driver information (auto-generated IDs)
- **vehicles**: Vehicle information
- **trips**: Delivery trips (ROUTE field as ID)
- **deliveries**: Sub-collection under trips (SHINUMBER as ID)

### Data Flow
1. `PollSageFunction` polls Sage 300 for all active clients
2. Transforms Sage data to Firestore schema
3. Creates trips and deliveries in Firestore
4. Mobile app consumes data for trip claiming and POD

### Key Design Principles
- Respects existing Sage 300 data structure
- Uses ROUTE field (or order number) for trip IDs
- Uses SHINUMBER for delivery IDs
- Multi-tenant with proper client separation
- Big bang migration from Windows service

## Configuration

### Local Development
Update `local.settings.json`:
- Set `GOOGLE_APPLICATION_CREDENTIALS`
- Set `PROJECT_ID` and `FIRESTORE_PROJECT_ID`
- Configure Sage 300 connection strings

### Production
- Uses Google Secret Manager for sensitive data
- Environment variables for configuration
- Service account authentication

## Completed Features
✅ **Complete Firestore Models**: All models match exact JSON schema  
✅ **Multi-tenant Sage Polling**: Polls multiple client Sage 300 databases  
✅ **Data Transformation**: Converts Sage shipments to Trip/Delivery format  
✅ **Google Cloud Functions**: PollSageFunction and UpdateDeliveryStatusFunction  
✅ **Address Geocoding**: Google Maps integration for GPS coordinates  
✅ **Business Logic**: Preserves ROUTE field priority, name cleaning, BILEMAIL usage  

## Functions Available
- **PollSageFunction**: HTTP-triggered, polls all active clients' Sage 300 databases
- **UpdateDeliveryStatusFunction**: HTTP-triggered, for mobile app delivery updates

## Next Steps
1. Set up GCP project and deploy functions
2. Configure Cloud Scheduler for automated polling
3. Set up monitoring and logging
4. Add authentication for mobile app endpoints
5. Implement complete POD data handling
