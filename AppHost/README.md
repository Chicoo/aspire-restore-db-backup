# Aspire SQL Server Database Restore

This project demonstrates how to restore a SQL Server database from a backup file stored in Azure File Share using .NET Aspire.

## Features

- **Automatic Backup Download**: Downloads database backup files from Azure File Share with progress tracking
- **Database Restoration**: Automatically restores SQL Server databases from `.bak` files
- **Container Management**: Uses Docker containers for SQL Server with persistent volumes
- **Aspire Dashboard Integration**: All logs and progress visible in the Aspire dashboard
- **DbGate Integration**: Includes DbGate for database management UI

## Prerequisites

- .NET 10 SDK
- Docker Desktop
- .NET Aspire workload installed
- Azure Storage Account with File Share (for backup files)

## Configuration

### appsettings.json

Configure the following settings in `appsettings.json` or `appsettings.Development.json`:

```json
{
  "DatabaseName": "your-database-name",
  "BackupFileName": "your-backup-file.bak",
  "AzureFileShare": {
    "FileUrl": "https://yourstorageaccount.file.core.windows.net/yourshare/your-backup-file.bak",
    "StorageAccountKey": "your-storage-account-key"
  }
}
```

### Configuration Parameters

- **DatabaseName**: The name of the database to create/restore
- **BackupFileName**: The name of the backup file (should match the file in Azure File Share)
- **AzureFileShare:FileUrl**: Full URL to the backup file in Azure File Share
- **AzureFileShare:StorageAccountKey**: Azure Storage Account access key for authentication

## How It Works

### 1. Download Phase
- Checks if backup file exists locally in `sqldata` directory
- Downloads from Azure File Share if not present using SharedKey authentication
- Reports download progress every 10% (visible in Aspire dashboard logs)
- Caches the file locally to avoid repeated downloads

### 2. SQL Server Setup
- Starts SQL Server 2022 container on port 5007
- Mounts local `sqldata` directory to `/var/opt/mssql/backup` in container
- Creates persistent data volume for database files
- Configures SQL Server with custom SA password

### 3. Database Restore
- Connects to SQL Server master database
- Checks if target database exists and contains tables
- Drops empty databases before restore
- Restores from backup file if database is empty or doesn't exist
- Moves database files to `/var/opt/mssql/data/` inside container
- Sets TRUSTWORTHY ON for the database
- Configures database owner as 'sa'

## Project Structure

```
aspire-restore-db-backup/
??? AppHost/
?   ??? AppHost.cs                    # Main application entry point
?   ??? SqlServerExtensions.cs        # Extension methods for database operations
?   ??? AppHost.csproj                # Project file
?   ??? appsettings.json             # Base configuration
?   ??? appsettings.Development.json # Development-specific configuration
?   ??? sqldata/                     # Local directory for backup files (auto-created)
??? README.md                        # This file
```

## Usage

### Running the Application

1. Clone the repository:
   ```bash
   git clone https://github.com/Chicoo/aspire-restore-db-backup.git
   cd aspire-restore-db-backup
   ```

2. Configure your Azure File Share settings in `AppHost/appsettings.Development.json`:
   ```json
   {
     "DatabaseName": "mydb",
     "BackupFileName": "mydb.bak",
     "AzureFileShare": {
       "FileUrl": "https://mystorageaccount.file.core.windows.net/backups/mydb.bak",
       "StorageAccountKey": "your-key-here"
     }
   }
   ```

3. Run the application:
   ```bash
   cd AppHost
   dotnet run
   ```

4. Access the Aspire dashboard at the URL shown in the console output (typically `https://localhost:17075`)

5. SQL Server will be available at `localhost:5007` with:
   - **Username**: `sa`
   - **Password**: `MySecureP@ssw0rd!` (or the value configured in the code)

### Connecting to the Database

Use any SQL Server client tool:
```
Server: localhost,5007
Username: sa
Password: MySecureP@ssw0rd!
Database: [your-database-name]
```

Or use the DbGate UI accessible through the Aspire dashboard.

## Extension Methods

The project provides two custom extension methods for `IResourceBuilder<SqlServerDatabaseResource>`:

### WithBackupDownload

Downloads a database backup file from Azure File Share:

```csharp
.WithBackupDownload(
    string fileUrl,              // Azure File Share URL
    string? storageAccountKey,   // Storage account access key
    string localFilePath,        // Local path to save the file
    string fileName              // Name of the backup file
)
```

**Features:**
- Skips download if file already exists locally
- Uses Azure SharedKey authentication
- Reports download progress every 10%
- Handles errors gracefully with logging

### WithDatabaseRestore

Restores a database from a backup file:

```csharp
.WithDatabaseRestore(
    string backupFilePath       // Container path to the backup file
)
```

**Features:**
- Checks if database already exists with data
- Drops and recreates empty databases
- Dynamically maps logical file names to physical files
- Sets database to TRUSTWORTHY mode
- Configures database owner as 'sa'

## Logging

All operations are logged to the Aspire dashboard under the database resource logs:

- Download start and completion with file size
- Download progress updates (every 10%)
- Database existence checks
- Restore operations and status
- Error messages with stack traces

## Notes

- **Backup File Caching**: Files are downloaded once and reused on subsequent runs
- **Container Persistence**: SQL Server container uses `ContainerLifetime.Persistent` to survive restarts
- **Data Volume**: Database files persist across container restarts via data volume
- **Conditional Restore**: Database restore only occurs if the database doesn't exist or is empty
- **Port Mapping**: SQL Server exposed on host port 5007

## Security Considerations

?? **Important Security Notes:**

- **Never commit** `appsettings.Development.json` with real credentials to source control
- Add `appsettings.Development.json` to `.gitignore`
- Use user secrets for local development:
  ```bash
  dotnet user-secrets set "AzureFileShare:StorageAccountKey" "your-key"
  ```
- Use Azure Key Vault or environment variables for production deployments
- The storage account key provides full access to your Azure Storage account
- Change the default SA password in production environments

## Troubleshooting

### Backup File Not Found Error
**Error**: `Cannot open backup device '/var/opt/mssql/backup/file.bak'. Operating system error 2`

**Solution**: 
- Ensure the backup file downloaded successfully to the `sqldata` directory
- Check Azure File Share URL and access key are correct
- Verify the `BackupFileName` matches the actual file name

### Connection Timeout
**Solution**:
- Ensure Docker Desktop is running
- Wait for SQL Server container to fully start (check Aspire dashboard logs)
- The code includes a 5-second delay to allow SQL Server to initialize

### Download Fails
**Solution**:
- Verify Azure File Share URL format is correct
- Check storage account key is valid
- Ensure network connectivity to Azure
- Review error messages in Aspire dashboard logs

## Dependencies

- **Aspire.Hosting**: .NET Aspire hosting package
- **Aspire.Hosting.SqlServer**: SQL Server hosting support
- **CommunityToolkit.Aspire.Hosting.SqlServer.Extensions**: SQL Server extensions
- **CommunityToolkit.Aspire.Hosting.DbGate**: DbGate integration
- **Microsoft.Data.SqlClient**: SQL Server data provider

## License

MIT

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

### Development Setup

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Test with a real Azure File Share
5. Submit a pull request

## Support

For issues and questions:
- Open an issue on [GitHub](https://github.com/Chicoo/aspire-restore-db-backup/issues)
- Check existing issues for similar problems

## Related Resources

- [.NET Aspire Documentation](https://learn.microsoft.com/dotnet/aspire/)
- [Azure File Share Documentation](https://learn.microsoft.com/azure/storage/files/)
- [SQL Server on Docker](https://learn.microsoft.com/sql/linux/quickstart-install-connect-docker)
