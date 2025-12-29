using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;


public static class SqlServerExtensions
{
    extension<T>(IResourceBuilder<T> builder) where T : SqlServerDatabaseResource
    {
        public IResourceBuilder<T> WithDatabaseRestore(string backupFilePath)
        {
            _ = builder.OnResourceReady(async (resourceBuilder, evt, ct) =>
            {
                var dbResource = evt.Resource as SqlServerDatabaseResource;
                if (dbResource != null)
                {
                    var logger = evt.Services
                        .GetRequiredService<ResourceLoggerService>()
                        .GetLogger(builder.Resource);

                    await RestoreDatabaseAsync(dbResource, backupFilePath, logger, ct);
                }
            });

            return builder;
        }

        public IResourceBuilder<T> WithBackupDownload(string fileUrl, string? storageAccountKey, string localFilePath, string fileName)
        {
            _ = builder.OnResourceReady(async (resourceBuilder, evt, ct) =>
            {
                var dbResource = evt.Resource as SqlServerDatabaseResource;
                if (dbResource != null)
                {
                    var logger = evt.Services
                        .GetRequiredService<ResourceLoggerService>()
                        .GetLogger(builder.Resource);

                    await DownloadDatabase(logger, storageAccountKey, localFilePath, fileUrl, fileName);
                }
            });

            return builder;
        }
    }

    private static async Task DownloadDatabase(ILogger logger, string? storageAccountKey, string localFilePath, string fileUrl, string fileName)
    {
        // Download database file from Azure File Share

        if (!File.Exists(localFilePath))
        {
            if (string.IsNullOrEmpty(storageAccountKey))
            {
                throw new ArgumentNullException(nameof(storageAccountKey), "Storage account key is required to access the Azure File Share.");
            }
            using var httpClient = new HttpClient();
            var uri = new Uri(fileUrl);
            var storageAccountName = uri.Host.Split('.')[0];
            var pathParts = uri.LocalPath.TrimStart('/').Split('/');
            var shareName = pathParts[0];
            var filePath = string.Join("/", pathParts.Skip(1));

            var date = DateTime.UtcNow.ToString("R");
            var request = new HttpRequestMessage(HttpMethod.Get, fileUrl);
            request.Headers.Add("x-ms-date", date);
            request.Headers.Add("x-ms-version", "2021-08-06");

            // SharedKey authentication
            var canonicalizedResource = $"/{storageAccountName}/{shareName}/{filePath}";
            var stringToSign = $"GET\n\n\n\n\n\n\n\n\n\n\n\nx-ms-date:{date}\nx-ms-version:2021-08-06\n{canonicalizedResource}";
            using var hmac = new System.Security.Cryptography.HMACSHA256(Convert.FromBase64String(storageAccountKey));
            var signature = Convert.ToBase64String(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(stringToSign)));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("SharedKey", $"{storageAccountName}:{signature}");

            try
            {
                logger.LogInformation($"Downloading {fileName} from Azure File Share...");
                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if (response.IsSuccessStatusCode)
                {
                    var totalBytes = response.Content.Headers.ContentLength ?? -1;
                    var canReportProgress = totalBytes != -1;

                    if (canReportProgress)
                    {
                        logger.LogInformation($"Total file size: {totalBytes / (1024.0 * 1024.0):F2} MB");
                    }

                    using var fileStream = File.Create(localFilePath);
                    using var contentStream = await response.Content.ReadAsStreamAsync();

                    var buffer = new byte[8192];
                    long totalBytesRead = 0;
                    int bytesRead;
                    var lastReportedProgress = 0;

                    while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                        totalBytesRead += bytesRead;

                        if (canReportProgress)
                        {
                            var progressPercentage = (int)((totalBytesRead * 100) / totalBytes);

                            // Report every 1% progress
                            if (progressPercentage >= lastReportedProgress + 1)
                            {
                                lastReportedProgress = progressPercentage;
                                logger.LogInformation($"Download progress: {progressPercentage}% ({totalBytesRead / (1024.0 * 1024.0):F2} MB / {totalBytes / (1024.0 * 1024.0):F2} MB)");
                            }
                        }
                    }

                    logger.LogInformation($"Downloaded {fileName} successfully to {localFilePath} ({totalBytesRead / (1024.0 * 1024.0):F2} MB)");
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    logger.LogInformation($"Failed to download {fileName}: {response.StatusCode} - {errorContent}");
                }
            }
            catch (Exception ex)
            {
                logger.LogInformation($"Error downloading {fileName}: {ex.Message}");
            }
        }
        else
        {
            logger.LogInformation($"{fileName} already exists locally at {localFilePath}");
        }

    }


    private static async Task RestoreDatabaseAsync(
        SqlServerDatabaseResource dbResource,
        string backupFile,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Starting database restore for {DatabaseName}...", dbResource.DatabaseName);

            var connectionString = await dbResource.ConnectionStringExpression.GetValueAsync(cancellationToken);
            if (connectionString == null)
            {
                logger.LogError("Could not get connection string for database {DatabaseName}", dbResource.DatabaseName);
                return;
            }

            var connectionStringBuilder = new SqlConnectionStringBuilder(connectionString);
            var databaseName = connectionStringBuilder.InitialCatalog;

            await Task.Delay(5000, cancellationToken);

            connectionStringBuilder.InitialCatalog = "master";
            using var masterConnection = new SqlConnection(connectionStringBuilder.ConnectionString);
            await masterConnection.OpenAsync(cancellationToken);

            logger.LogInformation("Connected to SQL Server, checking if database exists...");

            using var checkCmd = masterConnection.CreateCommand();
            checkCmd.CommandText = $"SELECT COUNT(*) FROM sys.databases WHERE name = '{databaseName}'";
            var exists = (int)(await checkCmd.ExecuteScalarAsync(cancellationToken) ?? 0) > 0;

            bool needsRestore = false;

            if (exists)
            {
                try
                {
                    using var killCmd = masterConnection.CreateCommand();
                    killCmd.CommandText = $@"
                        DECLARE @kill varchar(8000) = '';
                        SELECT @kill = @kill + 'KILL ' + CONVERT(varchar(5), session_id) + ';'
                        FROM sys.dm_exec_sessions
                        WHERE database_id = DB_ID('{databaseName}')
                        AND session_id <> @@SPID;
                        EXEC(@kill);
                        ALTER DATABASE [{databaseName}] SET MULTI_USER WITH ROLLBACK IMMEDIATE;";
                    await killCmd.ExecuteNonQueryAsync(cancellationToken);
                    logger.LogInformation("Killed connections and set database {DatabaseName} to multi-user mode", databaseName);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not reset database to multi-user mode");
                }

                using var tableCheckCmd = masterConnection.CreateCommand();
                tableCheckCmd.CommandText = $@"
                    SELECT COUNT(*) 
                    FROM [{databaseName}].sys.tables 
                    WHERE is_ms_shipped = 0";
                var tableCount = (int)(await tableCheckCmd.ExecuteScalarAsync(cancellationToken) ?? 0);

                if (tableCount == 0)
                {
                    logger.LogInformation("Database {DatabaseName} exists but is empty, will restore from backup.", databaseName);
                    needsRestore = true;

                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            using var dropCmd = masterConnection.CreateCommand();
                            dropCmd.CommandText = $@"
                                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                                DROP DATABASE [{databaseName}];";
                            await dropCmd.ExecuteNonQueryAsync(cancellationToken);
                            logger.LogInformation("Dropped empty database {DatabaseName}", databaseName);
                            break;
                        }
                        catch (SqlException ex) when (ex.Number == 3702 && i < 2)
                        {
                            logger.LogWarning("Database in use, waiting before retry {Attempt}/3", i + 1);
                            await Task.Delay(2000, cancellationToken);
                        }
                    }
                }
                else
                {
                    logger.LogInformation("Database {DatabaseName} already has {TableCount} tables, skipping restore.", databaseName, tableCount);
                }
            }
            else
            {
                logger.LogInformation("Database does not exist, will restore from backup.");
                needsRestore = true;
            }

            if (needsRestore)
            {
                logger.LogInformation("Restoring database from backup...");

                using var fileListCmd = masterConnection.CreateCommand();
                fileListCmd.CommandText = $"RESTORE FILELISTONLY FROM DISK = '{backupFile}'";
                using var reader = await fileListCmd.ExecuteReaderAsync(cancellationToken);

                var logicalNames = new List<(string LogicalName, string Type)>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    logicalNames.Add((reader.GetString(0), reader.GetString(2)));
                }
                await reader.CloseAsync();

                logger.LogInformation("Found {Count} files in backup", logicalNames.Count);

                var moveStatements = string.Join(",\n     ", logicalNames.Select((ln, index) =>
                {
                    var extension = ln.Type == "L" ? "_log.ldf" : ".mdf";
                    var physicalFileName = $"{databaseName}_{index}{extension}";
                    return $"MOVE '{ln.LogicalName}' TO '/var/opt/mssql/data/{physicalFileName}'";
                }));

                var restoreCommand = $@"
RESTORE DATABASE [{databaseName}]
FROM DISK = '{backupFile}'
WITH {moveStatements},
     REPLACE, RECOVERY";

                logger.LogInformation("Executing RESTORE command...");
                using var restoreCmd = masterConnection.CreateCommand();
                restoreCmd.CommandText = restoreCommand;
                restoreCmd.CommandTimeout = 300;
                await restoreCmd.ExecuteNonQueryAsync(cancellationToken);

                logger.LogInformation("Database {DatabaseName} restored successfully!", databaseName);

                logger.LogInformation("Setting TRUSTWORTHY ON for database {DatabaseName}...", databaseName);
                using var trustCmd = masterConnection.CreateCommand();
                trustCmd.CommandText = $"ALTER DATABASE [{databaseName}] SET TRUSTWORTHY ON;";
                await trustCmd.ExecuteNonQueryAsync(cancellationToken);

                using var ownerConnection = new SqlConnection(connectionString);
                await ownerConnection.OpenAsync(cancellationToken);
                using var ownerUpdate = ownerConnection.CreateCommand();
                ownerUpdate.CommandText = "EXEC sp_changedbowner 'sa';";
                await ownerUpdate.ExecuteNonQueryAsync(cancellationToken);

                logger.LogInformation("Database {DatabaseName} fully initialized!", databaseName);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error restoring database {DatabaseName}", dbResource.DatabaseName);
        }
    }
}