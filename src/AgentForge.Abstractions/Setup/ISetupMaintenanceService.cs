using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;

namespace AgentForge.Abstractions.Setup;

public interface ISetupMaintenanceService
{
    Task<DomainResult<SetupDoctorReport>> DoctorAsync(
        DoctorRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<ExportSetupProfileResult>> ExportAsync(
        ExportSetupProfileRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<RecoveryTransitionResult>> EnterRecoveryAsync(
        EnterRecoveryRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<RecoveryTransitionResult>> ResumeRecoveryAsync(
        ResumeRecoveryRequest request,
        CancellationToken cancellationToken);
}
