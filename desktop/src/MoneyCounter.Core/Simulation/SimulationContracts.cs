namespace MoneyCounter.Core.Simulation;

public sealed record CreateSimulationCommand(Guid OperationId, long Seed);
public sealed record ResetSimulationCommand(Guid OperationId, long DatasetId);
public sealed record SimulationDetail(long Id, long Seed, string Name, DateTimeOffset CreatedAtUtc, string GeneratorVersion);
public sealed record SimulationResetResult(long DatasetId, IReadOnlyDictionary<string, long> DeletedCounts);
public interface ISimulationService
{
    Task<Result<SimulationDetail>> CreateAsync(CreateSimulationCommand command, CancellationToken cancellationToken = default);
    Task<Result<SimulationResetResult>> ResetAsync(ResetSimulationCommand command, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<SimulationDetail>>> ListAsync(CancellationToken cancellationToken = default);
}
