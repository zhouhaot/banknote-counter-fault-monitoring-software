namespace MoneyCounter.Infrastructure.Simulation;

public static class SimulationMigration
{
    public const string Sql = """
        ALTER TABLE SimulationDataset ADD COLUMN Seed INTEGER;
        ALTER TABLE SimulationDataset ADD COLUMN Name TEXT NOT NULL DEFAULT '历史模拟集';
        ALTER TABLE SimulationDataset ADD COLUMN CreatedAtUtc TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000Z';
        ALTER TABLE SimulationDataset ADD COLUMN GeneratorVersion TEXT NOT NULL DEFAULT 'legacy';
        CREATE UNIQUE INDEX UX_SimulationDataset_Seed ON SimulationDataset(Seed);
        DROP TRIGGER InventoryMovement_NoDelete;
        CREATE TRIGGER InventoryMovement_NoDelete BEFORE DELETE ON InventoryMovement
        WHEN NOT (OLD.Source='SIMULATED' AND OLD.SimulationDatasetId IS simulation_reset_dataset())
        BEGIN SELECT RAISE(ABORT,'Inventory ledger is immutable'); END;
        CREATE TRIGGER Device_ValidateSource_Insert BEFORE INSERT ON Device
        WHEN NOT EXISTS(SELECT 1 FROM Model m WHERE m.Id=NEW.ModelId AND (m.Source='SIMULATED')=(NEW.Source='SIMULATED') AND (m.Source<>'SIMULATED' OR m.SimulationDatasetId=NEW.SimulationDatasetId))
        BEGIN SELECT RAISE(ABORT,'Invalid model source'); END;
        CREATE TRIGGER Device_ValidateSource_Update BEFORE UPDATE OF ModelId,Source,SimulationDatasetId ON Device
        WHEN NOT EXISTS(SELECT 1 FROM Model m WHERE m.Id=NEW.ModelId AND (m.Source='SIMULATED')=(NEW.Source='SIMULATED') AND (m.Source<>'SIMULATED' OR m.SimulationDatasetId=NEW.SimulationDatasetId))
        BEGIN SELECT RAISE(ABORT,'Invalid model source'); END;
        """;
}
