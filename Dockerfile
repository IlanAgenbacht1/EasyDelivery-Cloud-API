# Use the official .NET 8 runtime as base image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

# Use the .NET 8 SDK for building
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["DeliveryCloudApi.csproj", "./"]
RUN dotnet restore "DeliveryCloudApi.csproj"

# Copy all source files
COPY . .

# Build the application
RUN dotnet build "DeliveryCloudApi.csproj" -c Release -o /app/build

# Publish the application
FROM build AS publish
RUN dotnet publish "DeliveryCloudApi.csproj" -c Release -o /app/publish

# Create final runtime image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Set environment variables for Cloud Run
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Create a non-root user for security
RUN adduser --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

# Entry point for ASP.NET Core Web API
ENTRYPOINT ["dotnet", "DeliveryCloudApi.dll"]
