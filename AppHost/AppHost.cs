using Microsoft.Extensions.Logging;

var builder = DistributedApplication.CreateBuilder(args);

// Azure File Share configuration
var fileUrl = builder.Configuration["AzureFileShare:FileUrl"];

if (string.IsNullOrEmpty(fileUrl))
{
    Console.WriteLine("Azure File Share URL is not configured. Please set 'AzureFileShare:FileUrl' in appsettings.json.");
    return;
}

var storageAccountKey = builder.Configuration["AzureFileShare:StorageAccountKey"];
var localPath = Path.Combine(Directory.GetCurrentDirectory(), "sqldata");
var fileName = Path.GetFileName(new Uri(fileUrl).LocalPath);
var localFilePath = Path.Combine(localPath, fileName);

// Ensure local directory exists
Directory.CreateDirectory(localPath);

var saPassword = builder.AddParameter("sapassword", "MySecureP@ssw0rd!");

var sqlserver = builder.AddSqlServer("sqlserver")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume()
    .WithDbGate()
    .WithOtlpExporter()
    .WithHostPort(5007)
    .WithPassword(saPassword)
    .WithBindMount(localPath, "/var/opt/mssql/backup");

//Add a database that will be restored from the backup
var databaseName = builder.Configuration["DatabaseName"];
var backupFileName = builder.Configuration["BackupFileName"];
var backupFilePath = $"/var/opt/mssql/backup/{backupFileName}";
var db = sqlserver
    .AddDatabase(databaseName)
    .WithBackupDownload(fileUrl, storageAccountKey, localFilePath, fileName)
    .WithDatabaseRestore(backupFilePath);

builder.Build().Run();

