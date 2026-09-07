namespace MoneyCounter.Infrastructure.Inventory;

public static class InventoryMigration
{
    public const string Sql = """
        CREATE TABLE Consumable(
          Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE CHECK(length(trim(Name)) BETWEEN 1 AND 120),
          Unit TEXT NOT NULL CHECK(length(trim(Unit)) BETWEEN 1 AND 32), Notes TEXT NOT NULL CHECK(length(Notes)<=2000),
          IsActive INTEGER NOT NULL DEFAULT 1 CHECK(IsActive IN(0,1)), Revision INTEGER NOT NULL DEFAULT 0 CHECK(Revision>=0),
          Source TEXT NOT NULL CHECK(Source IN('MANUAL','CSV','SIMULATED')), ImportBatchId INTEGER REFERENCES ImportBatch(Id), SimulationDatasetId INTEGER REFERENCES SimulationDataset(Id),
          CHECK((Source='MANUAL' AND ImportBatchId IS NULL AND SimulationDatasetId IS NULL) OR (Source='CSV' AND ImportBatchId IS NOT NULL AND SimulationDatasetId IS NULL) OR (Source='SIMULATED' AND ImportBatchId IS NULL AND SimulationDatasetId IS NOT NULL)));
        CREATE TABLE InventoryMovement(
          Id INTEGER PRIMARY KEY, ConsumableId INTEGER NOT NULL REFERENCES Consumable(Id),
          MovementType TEXT NOT NULL CHECK(MovementType IN('INBOUND','ISSUE','RETURN','ADJUST','REVERSAL')),
          QuantityMinor INTEGER NOT NULL CHECK(typeof(QuantityMinor)='integer' AND QuantityMinor BETWEEN -999999999999 AND 999999999999 AND QuantityMinor<>0),
          OccurredAt TEXT NOT NULL, Reason TEXT NOT NULL CHECK(length(trim(Reason)) BETWEEN 1 AND 500),
          DeviceId INTEGER REFERENCES Device(Id), FaultId INTEGER REFERENCES Fault(Id), ReversesId INTEGER UNIQUE REFERENCES InventoryMovement(Id),
          Source TEXT NOT NULL CHECK(Source IN('MANUAL','CSV','SIMULATED')), ImportBatchId INTEGER REFERENCES ImportBatch(Id), SimulationDatasetId INTEGER REFERENCES SimulationDataset(Id), CreatedAtUtc TEXT NOT NULL,
          CHECK((MovementType IN('INBOUND','RETURN') AND QuantityMinor>0) OR (MovementType='ISSUE' AND QuantityMinor<0) OR MovementType IN('ADJUST','REVERSAL')),
          CHECK(MovementType<>'ISSUE' OR DeviceId IS NOT NULL), CHECK(FaultId IS NULL OR DeviceId IS NOT NULL),
          CHECK((MovementType='REVERSAL')=(ReversesId IS NOT NULL)),
          CHECK((Source='MANUAL' AND ImportBatchId IS NULL AND SimulationDatasetId IS NULL) OR (Source='CSV' AND ImportBatchId IS NOT NULL AND SimulationDatasetId IS NULL) OR (Source='SIMULATED' AND ImportBatchId IS NULL AND SimulationDatasetId IS NOT NULL)));
        CREATE INDEX IX_InventoryMovement_Consumable ON InventoryMovement(ConsumableId,OccurredAt DESC,Id DESC);
        CREATE INDEX IX_InventoryMovement_Device ON InventoryMovement(DeviceId,OccurredAt DESC);
        CREATE INDEX IX_InventoryMovement_Fault ON InventoryMovement(FaultId,OccurredAt DESC);
        CREATE TRIGGER InventoryMovement_NoUpdate BEFORE UPDATE ON InventoryMovement BEGIN SELECT RAISE(ABORT,'Inventory ledger is immutable'); END;
        CREATE TRIGGER InventoryMovement_NoDelete BEFORE DELETE ON InventoryMovement BEGIN SELECT RAISE(ABORT,'Inventory ledger is immutable'); END;
        CREATE TRIGGER InventoryMovement_Validate BEFORE INSERT ON InventoryMovement BEGIN
          SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM Consumable c WHERE c.Id=NEW.ConsumableId AND (c.IsActive=1 OR NEW.ReversesId IS NOT NULL) AND (c.Source='SIMULATED')=(NEW.Source='SIMULATED') AND (c.Source<>'SIMULATED' OR c.SimulationDatasetId=NEW.SimulationDatasetId)) THEN RAISE(ABORT,'Invalid consumable source or state') END;
          SELECT CASE WHEN NEW.DeviceId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Device d WHERE d.Id=NEW.DeviceId AND (d.Source='SIMULATED')=(NEW.Source='SIMULATED') AND (d.Source<>'SIMULATED' OR d.SimulationDatasetId=NEW.SimulationDatasetId)) THEN RAISE(ABORT,'Invalid device source') END;
          SELECT CASE WHEN NEW.FaultId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Fault f WHERE f.Id=NEW.FaultId AND f.DeviceId=NEW.DeviceId AND (f.Source='SIMULATED')=(NEW.Source='SIMULATED') AND (f.Source<>'SIMULATED' OR f.SimulationDatasetId=NEW.SimulationDatasetId)) THEN RAISE(ABORT,'Invalid fault association') END;
          SELECT CASE WHEN NEW.ReversesId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM InventoryMovement m WHERE m.Id=NEW.ReversesId AND m.ConsumableId=NEW.ConsumableId AND m.QuantityMinor=-NEW.QuantityMinor AND m.DeviceId IS NEW.DeviceId AND m.FaultId IS NEW.FaultId AND NEW.OccurredAt>=m.OccurredAt AND (m.Source='SIMULATED')=(NEW.Source='SIMULATED') AND (m.Source<>'SIMULATED' OR m.SimulationDatasetId=NEW.SimulationDatasetId)) THEN RAISE(ABORT,'Invalid reversal') END;
          SELECT CASE WHEN NEW.QuantityMinor<0 AND COALESCE((SELECT SUM(QuantityMinor) FROM InventoryMovement WHERE ConsumableId=NEW.ConsumableId),0)<-NEW.QuantityMinor THEN RAISE(ABORT,'Insufficient stock') END;
          SELECT CASE WHEN NEW.QuantityMinor>0 AND COALESCE((SELECT SUM(QuantityMinor) FROM InventoryMovement WHERE ConsumableId=NEW.ConsumableId),0)>9223372036854775807-NEW.QuantityMinor THEN RAISE(ABORT,'Stock overflow') END;
        END;
        CREATE TRIGGER Consumable_KeepHistory BEFORE DELETE ON Consumable WHEN EXISTS(SELECT 1 FROM InventoryMovement WHERE ConsumableId=OLD.Id) BEGIN SELECT RAISE(ABORT,'Consumable has history'); END;
        CREATE TRIGGER Consumable_StableSource BEFORE UPDATE OF Source,ImportBatchId,SimulationDatasetId,Unit ON Consumable WHEN EXISTS(SELECT 1 FROM InventoryMovement WHERE ConsumableId=OLD.Id) AND (NEW.Source<>OLD.Source OR NEW.ImportBatchId IS NOT OLD.ImportBatchId OR NEW.SimulationDatasetId IS NOT OLD.SimulationDatasetId OR NEW.Unit<>OLD.Unit) BEGIN SELECT RAISE(ABORT,'Consumable history source and unit are fixed'); END;
        """;
}
