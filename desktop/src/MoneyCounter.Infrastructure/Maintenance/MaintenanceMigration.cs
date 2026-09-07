namespace MoneyCounter.Infrastructure.Maintenance;
public static class MaintenanceMigration
{
    public const string Sql = """
        CREATE TABLE Fault(
          Id INTEGER PRIMARY KEY, FaultNo TEXT NOT NULL UNIQUE, DeviceId INTEGER NOT NULL REFERENCES Device(Id),
          SourceAnomalyId INTEGER UNIQUE REFERENCES Anomaly(Id), RegisteredAt TEXT NOT NULL,
          FaultType TEXT NOT NULL CHECK(FaultType IN ('CARD_JAM','COUNTING','AUTHENTICATION','FEED','DISPLAY','POWER','MECHANICAL','OTHER')),
          Severity TEXT NOT NULL CHECK(Severity IN ('LOW','MEDIUM','HIGH','URGENT')),
          Description TEXT NOT NULL CHECK(length(trim(Description)) BETWEEN 1 AND 2000),
          Status TEXT NOT NULL CHECK(Status IN ('PENDING','IN_PROGRESS','CLOSED')), StartedAt TEXT, ClosedAt TEXT,
          FinalResult TEXT NOT NULL DEFAULT '', Revision INTEGER NOT NULL DEFAULT 0 CHECK(Revision>=0),
          Source TEXT NOT NULL CHECK(Source IN ('MANUAL','CSV','SIMULATED')), ImportBatchId INTEGER REFERENCES ImportBatch(Id), SimulationDatasetId INTEGER REFERENCES SimulationDataset(Id), CreatedAtUtc TEXT NOT NULL,
          CHECK((Status='PENDING' AND StartedAt IS NULL AND ClosedAt IS NULL AND FinalResult='') OR (Status='IN_PROGRESS' AND StartedAt IS NOT NULL AND StartedAt>=RegisteredAt AND ClosedAt IS NULL AND FinalResult='') OR (Status='CLOSED' AND StartedAt IS NOT NULL AND ClosedAt IS NOT NULL AND StartedAt>=RegisteredAt AND ClosedAt>=StartedAt AND length(trim(FinalResult)) BETWEEN 1 AND 2000)),
          CHECK((Source='MANUAL' AND ImportBatchId IS NULL AND SimulationDatasetId IS NULL) OR (Source='CSV' AND ImportBatchId IS NOT NULL AND SimulationDatasetId IS NULL) OR (Source='SIMULATED' AND ImportBatchId IS NULL AND SimulationDatasetId IS NOT NULL)));
        CREATE INDEX IX_Fault_Device ON Fault(DeviceId,Status,RegisteredAt DESC);
        CREATE INDEX IX_Fault_Time ON Fault(RegisteredAt DESC,Id DESC);
        CREATE TABLE Repair(
          Id INTEGER PRIMARY KEY, FaultId INTEGER NOT NULL REFERENCES Fault(Id), RepairedAt TEXT NOT NULL,
          Action TEXT NOT NULL CHECK(length(trim(Action)) BETWEEN 1 AND 2000), Result TEXT NOT NULL CHECK(length(trim(Result)) BETWEEN 1 AND 2000),
          Technician TEXT NOT NULL CHECK(length(Technician)<=100), Notes TEXT NOT NULL CHECK(length(Notes)<=2000),
          Source TEXT NOT NULL CHECK(Source IN ('MANUAL','CSV','SIMULATED')), ImportBatchId INTEGER REFERENCES ImportBatch(Id), SimulationDatasetId INTEGER REFERENCES SimulationDataset(Id), CreatedAtUtc TEXT NOT NULL,
          CHECK((Source='MANUAL' AND ImportBatchId IS NULL AND SimulationDatasetId IS NULL) OR (Source='CSV' AND ImportBatchId IS NOT NULL AND SimulationDatasetId IS NULL) OR (Source='SIMULATED' AND ImportBatchId IS NULL AND SimulationDatasetId IS NOT NULL)));
        CREATE INDEX IX_Repair_Fault ON Repair(FaultId,RepairedAt,Id);
        """;
}
