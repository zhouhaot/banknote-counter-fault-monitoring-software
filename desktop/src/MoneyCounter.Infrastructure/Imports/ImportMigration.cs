namespace MoneyCounter.Infrastructure.Imports;

public static class ImportMigration
{
    public const string Sql = """
        ALTER TABLE ImportBatch ADD COLUMN Kind TEXT NOT NULL DEFAULT '';
        ALTER TABLE ImportBatch ADD COLUMN FileName TEXT NOT NULL DEFAULT '';
        ALTER TABLE ImportBatch ADD COLUMN Sha256 TEXT NOT NULL DEFAULT '';
        ALTER TABLE ImportBatch ADD COLUMN RowCount INTEGER NOT NULL DEFAULT 0 CHECK(RowCount>=0);
        ALTER TABLE ImportBatch ADD COLUMN ImportedAtUtc TEXT NOT NULL DEFAULT '';
        """;
}
