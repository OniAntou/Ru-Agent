# Ru documentation

This index separates current system documentation from dated implementation
records. Current documents describe what operators and maintainers should rely
on now; historical records explain how earlier changes were reached and are not
an operating contract.

## Current

- [Architecture](architecture.md)
- [Domain language](../CONTEXT.md)
- [Current runtime behavior](operations/runtime-behavior.md)
- [Operations and verification](operations/verification.md)
- [Agent tools and truthful execution](operations/agent-tools.md)
- [ASP.NET Core public route contract](aspnetcore-route-matrix.md)
- [Architecture decisions](adr/)
- [Canonical route data](../backend-dotnet/public-route-contract.json)
- [Local conversational-model retirement](adr/0005-retire-legacy-llama-line.md)

## Approved future work

- [OpenCode/Pi coding-loop parity design](superpowers/specs/2026-08-24-ru-opencode-pi-coding-loop-parity-design.md)
- [OpenCode/Pi coding-loop parity implementation program](superpowers/plans/2026-08-24-ru-opencode-pi-coding-loop-parity.md)

## Historical implementation records

- [FastAPI decommission validation](aspnetcore-cutover-validation.md)
- [2026-08-24 codebase security and reliability remediation design](superpowers/specs/2026-08-24-codebase-security-reliability-remediation-design.md)
- [2026-08-24 codebase security and reliability remediation plan](superpowers/plans/2026-08-24-codebase-security-reliability-remediation.md)
- [Action execution kernel v1 design](superpowers/specs/2026-08-22-ru-action-execution-kernel-v1-design.md)
- [Action execution kernel v1 implementation plan](superpowers/plans/2026-08-22-ru-action-execution-kernel-v1.md)
- [Superseded volume vertical-slice design](superpowers/specs/2026-08-22-ru-volume-tool-vertical-slice-design.md)
- [Superseded volume vertical-slice plan](superpowers/plans/2026-08-22-ru-volume-tool-vertical-slice.md)
- [Agent tools and skills roadmap](superpowers/plans/2026-08-22-ru-agent-tools-skills.md)

Superseded local-model, training, and evaluation plans were removed after the
current decisions were consolidated into the architecture, ADRs, runtime
behavior, and verification documents. Git history remains the recovery source
for implementation records.
