namespace MoneyCounter.Infrastructure.Operations;

public static class OperationsMigration
{
    public const string Sql = """
        CREATE TABLE StatusRecord (
          Id INTEGER PRIMARY KEY, DeviceId INTEGER NOT NULL REFERENCES Device(Id), RecordedAt TEXT NOT NULL,
          Status TEXT NOT NULL CHECK(Status IN ('RUNNING','STOPPED','FAULT','MAINTENANCE','RETIRED')),
          CumulativeCount INTEGER NOT NULL CHECK(CumulativeCount>=0), Notes TEXT NOT NULL,
          Source TEXT NOT NULL CHECK(Source IN ('MANUAL','CSV','SIMULATED')),
          ImportBatchId INTEGER REFERENCES ImportBatch(Id), SimulationDatasetId INTEGER REFERENCES SimulationDataset(Id), CreatedAtUtc TEXT NOT NULL,
          CHECK((Source='MANUAL' AND ImportBatchId IS NULL AND SimulationDatasetId IS NULL) OR (Source='CSV' AND ImportBatchId IS NOT NULL AND SimulationDatasetId IS NULL) OR (Source='SIMULATED' AND ImportBatchId IS NULL AND SimulationDatasetId IS NOT NULL)),
          UNIQUE(DeviceId,RecordedAt));
        CREATE INDEX IX_Status_Time ON StatusRecord(RecordedAt DESC,Id DESC);
        CREATE TABLE Anomaly (
          Id INTEGER PRIMARY KEY, DeviceId INTEGER NOT NULL REFERENCES Device(Id), DiscoveredAt TEXT NOT NULL,
          Description TEXT NOT NULL CHECK(length(trim(Description)) BETWEEN 1 AND 2000), Status TEXT NOT NULL CHECK(Status IN ('OPEN','CLOSED','CONVERTED')),
          HandlingNotes TEXT NOT NULL DEFAULT '', ClosedAt TEXT, Revision INTEGER NOT NULL DEFAULT 0 CHECK(Revision>=0),
          Source TEXT NOT NULL CHECK(Source IN ('MANUAL','CSV','SIMULATED')),
          ImportBatchId INTEGER REFERENCES ImportBatch(Id), SimulationDatasetId INTEGER REFERENCES SimulationDataset(Id), CreatedAtUtc TEXT NOT NULL,
          CHECK((Status='CLOSED' AND ClosedAt IS NOT NULL AND ClosedAt>=DiscoveredAt AND length(trim(HandlingNotes))>0) OR (Status IN ('OPEN','CONVERTED') AND ClosedAt IS NULL AND HandlingNotes='')),
          CHECK((Source='MANUAL' AND ImportBatchId IS NULL AND SimulationDatasetId IS NULL) OR (Source='CSV' AND ImportBatchId IS NOT NULL AND SimulationDatasetId IS NULL) OR (Source='SIMULATED' AND ImportBatchId IS NULL AND SimulationDatasetId IS NOT NULL)));
        CREATE INDEX IX_Anomaly_Device ON Anomaly(DeviceId,Status,DiscoveredAt DESC);
        CREATE INDEX IX_Anomaly_Time ON Anomaly(DiscoveredAt DESC,Id DESC);
        """;
}
