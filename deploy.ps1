# Deployment script for Delivery Cloud Functions to South Africa
# This script deploys the ASP.NET Core Web API to Google Cloud Run in africa-south1

param(
    [string]$ProjectId = "easydelivery-433910",
    [string]$Region = "africa-south1",
    [string]$ServiceName = "delivery-cloud-functions"
)

Write-Host "🚀 Deploying Delivery Cloud Functions to Google Cloud Run in South Africa" -ForegroundColor Green
Write-Host "   Project: $ProjectId" -ForegroundColor Yellow
Write-Host "   Region:  $Region" -ForegroundColor Yellow
Write-Host "   Service: $ServiceName" -ForegroundColor Yellow
Write-Host ""

# Set the Google Cloud project
Write-Host "Setting Google Cloud project..." -ForegroundColor Cyan
gcloud config set project $ProjectId

# Check if Artifact Registry repository exists, create if not
Write-Host "Checking Artifact Registry repository..." -ForegroundColor Cyan
try {
    gcloud artifacts repositories describe delivery-repo --location=$Region --project=$ProjectId 2>$null
    Write-Host "Artifact Registry repository already exists" -ForegroundColor Green
} catch {
    Write-Host "Creating Artifact Registry repository..." -ForegroundColor Yellow
    gcloud artifacts repositories create delivery-repo `
        --repository-format=docker `
        --location=$Region `
        --project=$ProjectId
}

# Enable required APIs
Write-Host "Enabling required APIs..." -ForegroundColor Cyan
gcloud services enable cloudbuild.googleapis.com --project=$ProjectId
gcloud services enable run.googleapis.com --project=$ProjectId
gcloud services enable artifactregistry.googleapis.com --project=$ProjectId

# Submit the build
Write-Host "Starting Cloud Build deployment..." -ForegroundColor Cyan
gcloud builds submit --config cloudbuild.yaml . --project=$ProjectId

# Get the deployed service URL
Write-Host ""
Write-Host "Getting service URL..." -ForegroundColor Cyan
$serviceUrl = gcloud run services describe $ServiceName --region=$Region --project=$ProjectId --format="value(status.url)" 2>$null

if ($serviceUrl) {
    Write-Host ""
    Write-Host "🎉 Deployment completed successfully!" -ForegroundColor Green
    Write-Host "   Service URL: $serviceUrl" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "API Endpoints:" -ForegroundColor Cyan
    Write-Host "   Health Check:        $serviceUrl/health" -ForegroundColor White
    Write-Host "   Receive Shipments:   $serviceUrl/receive-shipments (POST)" -ForegroundColor White
    Write-Host "   Update Delivery:     $serviceUrl/update-delivery-status (POST)" -ForegroundColor White
    Write-Host "   Swagger UI:          $serviceUrl/swagger" -ForegroundColor White
} else {
    Write-Host "❌ Could not retrieve service URL. Check the deployment logs above." -ForegroundColor Red
}

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "1. Deploy push agents to client servers" -ForegroundColor White
Write-Host "2. Configure client API keys in Firestore" -ForegroundColor White
Write-Host "3. Test the push agent integration" -ForegroundColor White
