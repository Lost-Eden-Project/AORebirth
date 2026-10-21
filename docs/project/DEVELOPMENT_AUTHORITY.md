# AORebirth Development Authority

Status: Permanent project policy.

## Authority

AORebirth has one authoritative gameplay source tree and one authoritative codebase.
Product/runtime source is maintained in public GitHub master. The private Linux
checkout owns the derived Linux source/build/package and required Linux-only
adaptations, infrastructure and deployment tooling. It records the exact accepted
public source SHA without becoming an independent product implementation.
Windows is the authoritative development platform and source of truth. Linux is
the production deployment platform.

There is no separate Linux implementation and no separate Windows
implementation. Linux hosts builds produced from the same AORebirth source tree;
it must never diverge into an independent implementation.

This policy governs future AI agents, developers, contributors, validation, and
production promotion. It complements the system description in
`docs/project/ARCHITECTURE.md` and the approved commands in
`docs/ai/WORKFLOW.md`.

## Checkout ownership and deployment-tooling access

<!-- deployment-tooling-access:start -->
Normal source work stays in its owning checkout: PUBLIC_PRODUCT_CHECKOUT owns
Windows/public product source; PRIVATE_LINUX_CHECKOUT owns the private derived
Linux source, build and package; APPROVED_DEPLOYMENT_TOOLING_ROOT supplies only
the established production deployment tooling. Exact private role bindings stay
in private governance, never in public commits.

Never silently edit product/runtime source in another checkout. The deployment
tooling checkout is not product authority. Linux adaptations, infrastructure,
history, configuration, credentials and protected GameData remain private.

Explicit deployment authorization itself is sufficient authorization to use
APPROVED_DEPLOYMENT_TOOLING_ROOT; no additional checkout-root waiver is required.
It permits reading the launcher and required supporting deployment scripts and
manifests, executing the approved deployment workflow, and using the existing
production SSH target and credentials already authorized for that deployment.
Do not ask again for a tooling-checkout waiver or an already-authorized connection.

This read/execute exception is limited to the approved tooling directory and
required supporting files that resolve within it. It grants no access through
parent traversal, sibling directories or symlink escapes. It does not permit
product/runtime edits, product rebuilds, unrelated file changes, or treating the
tooling checkout as product authority. Tooling edits require their own task
authorization. Existing workflow approval and private publication rules remain
in force; this exception does not revive retired deployment workflows.

Without explicit deployment authorization, this exception grants no deployment
tooling access or production connection use. Build/package approval, historical
deployment approval and mere possession of credentials are insufficient. A
current instruction not to deploy takes precedence. Separately authorized
governance/tooling maintenance does not authorize a deployment.

The following access contract governs only this deployment exception:

| Action | EXPLICIT_DEPLOYMENT_AUTHORIZATION | NO_DEPLOYMENT_AUTHORIZATION |
| --- | --- | --- |
| READ_LAUNCHER | ALLOW | DENY |
| READ_REQUIRED_SUPPORTING_DEPLOYMENT_FILES | ALLOW | DENY |
| EXECUTE_APPROVED_DEPLOYMENT_WORKFLOW | ALLOW | DENY |
| USE_EXISTING_AUTHORIZED_PRODUCTION_SSH | ALLOW | DENY |
| READ_OUTSIDE_APPROVED_TOOLING_ROOT | DENY | DENY |
| EDIT_PRODUCT_RUNTIME_SOURCE_IN_TOOLING_CHECKOUT | DENY | DENY |
| REBUILD_PRODUCT_SOURCE_IN_TOOLING_CHECKOUT | DENY | DENY |
| MODIFY_UNRELATED_FILES_IN_TOOLING_CHECKOUT | DENY | DENY |
| EDIT_DEPLOYMENT_TOOLING_WITHOUT_SEPARATE_TASK_AUTHORIZATION | DENY | DENY |
| TREAT_TOOLING_CHECKOUT_AS_PRODUCT_AUTHORITY | DENY | DENY |
| PUBLISH_PRIVATE_LINUX_MATERIAL_PUBLICLY | DENY | DENY |
<!-- deployment-tooling-access:end -->

Validate this governance contract with
Tools/tests/test_deployment_tooling_governance.py; it reads policy only and never
executes deployment tooling or contacts production.

## Windows authority

All development work originates on Windows, including:

- architecture and design;
- programming and debugging;
- unit and integration testing;
- regression verification;
- acceptance testing;
- approval of work for deployment.

All feature work begins in the authoritative Windows source tree. Windows
validation remains the acceptance gate for production.

## Linux role

Linux exists to:

- host production services;
- execute validated builds;
- run production infrastructure;
- perform deployment verification;
- perform platform-compatibility validation.

Linux is not the primary development or implementation environment. Linux
verification is an additional production gate, not a replacement for Windows
development, testing, or acceptance.

Linux-specific work must remain limited to operating-system concerns such as
deployment, hosting, packaging, service management, monitoring, permissions,
filesystem layout, and platform integration. It must not create Linux-only core
behavior.

## Listener exposure

Behavior belongs to source. Reachability belongs to explicit deployment
configuration.

LoginEngine and ZoneEngine_New use the governed `AO_REBIRTH_BIND_MODE`
configuration owner for client listener exposure. Accepted values are
`Loopback` and `Public`. An absent setting selects `Loopback`, which binds
`127.0.0.1`; `Public` binds `0.0.0.0`. Invalid or whitespace-only values fail
startup. Linux does not automatically mean public exposure, and Windows does
not automatically mean loopback beyond the same default policy. Validation
environments must remain loopback unless a focused test explicitly selects
`Public`.

When LoginEngine runs in `Loopback` mode, its advertised `ZoneIP` must remain a
loopback address. When LoginEngine runs in `Public` mode, `ZoneIP` must be a
concrete non-loopback address so the official client receives a usable
ZoneEngine_New handoff target. `0.0.0.0` is a listener bind address, not an
advertised client destination.

Public mode changes listener reachability only. It does not authorize packet,
gameplay, authentication, database schema, or database exposure changes. MySQL,
ISCom, validation, debug, and management listeners remain private unless a
separate audited policy explicitly changes them.

## Authoritative workflow

```text
Windows development
        |
Implementation
        |
Unit testing
        |
Integration testing
        |
Regression testing
        |
Acceptance testing
        |
Approved Windows-validated source/build
        |
Linux deployment build
        |
Linux compatibility and deployment verification
        |
Production promotion
```

Every production deployment must originate from Windows-validated source.
Linux deployment verification may block promotion, but it does not supersede
the Windows acceptance result.

## High-concurrency source synchronization

AORebirth uses one authoritative product history for Windows and Linux. Private
Linux adaptation history remains in its owning repository. Developers may work on
many short-lived branches, but Linux synchronization consumes integrated
`master` commits, not developer workspaces and not copied directories.

The required source identity chain is:

```text
developer branch
        |
        v
Windows validation and review
        |
        v
master integration commit
        |
        v
exact source SHA
        |
        v
controlled Linux build workspace
        |
        v
Linux acceptance artifact
        |
        v
SHA-gated deployment
```

The invariant for the public product source recorded by a cross-platform
acceptance event is:

```text
WINDOWS_ACCEPTED_PUBLIC_SHA == LINUX_PUBLIC_SOURCE_SHA == PACKAGE_PUBLIC_SOURCE_SHA
```

For a release, record the authoritative public source SHA separately from the
derived private Linux source and package SHAs. Runtime source must match that
accepted public source, with only documented Linux build/platform adaptations.
Build/package and running-engine receipts must identify the exact accepted
inputs. Keep server secrets and active host configuration private; product/runtime
repairs go through public master first. The tooling checkout supplies operational
files only and never changes the accepted product source authority.

Linux acceptance workspaces may perform destructive checkout cleanup only when
they are explicitly controlled build workspaces. Normal Windows developer
worktrees must never be reset or cleaned by synchronization tooling.

Production Linux source mutation is forbidden. Production consumes an accepted
artifact with provenance and an exact source SHA; it is not a development
workstation and must not become the authority for source edits.

Generated source remains governed by its explicit ownership model. Committed
generated artifacts are accepted as source only after their repository-owned
generator and validation workflow has produced the checked-in output. Linux
consumes those committed generated files from the exact Git SHA. Generated
artifacts that are intentionally produced during a build must be deterministic
from tracked inputs and must be covered by the relevant build provenance.

If Linux exposes a platform-specific defect, the repair path is:

```text
Linux finding
        |
        v
normal Windows source repair
        |
        v
Windows validation
        |
        v
master integration commit
        |
        v
Linux consumes new exact SHA
```

Direct source edits on Linux followed by later copy-back are not an accepted
synchronization model.

## Permanent rules

1. Windows is the authoritative development platform.
2. Linux is the production deployment target.
3. All feature work begins on Windows.
4. A bug discovered on Linux must be corrected in the Windows source tree first,
   unless evidence proves it is exclusively a Linux deployment or hosting issue.
5. Linux must never become the primary implementation environment.
6. Authentication logic must remain functionally identical across platforms.
7. Gameplay behavior must remain functionally identical across platforms.
8. Packet behavior must remain functionally identical across platforms.
9. Database behavior must remain functionally identical across platforms unless
   a proven platform-specific infrastructure requirement applies.
10. Linux-specific source changes are restricted to deployment, hosting,
    packaging, service management, monitoring, permissions, filesystem layout,
    and other operating-system boundaries.
11. The production Linux server must never diverge into an independent
    implementation.
12. Every production deployment must originate from Windows-validated source.
13. Windows validation is the production acceptance gate.
14. Linux verification is an additional gate, not a replacement for Windows
    validation.
15. Product/runtime source fixes required by a Linux deployment must originate
    in the authoritative Windows source tree. Private Linux build, packaging and
    infrastructure-only corrections stay in their owning private checkout.
16. AORebirth shall retain exactly one authoritative codebase.
17. Linux build and deployment tooling must use explicit source SHAs, not
    implicit moving branch tips.
18. Deployment must refuse new production artifacts whose accepted Linux
    provenance does not match the requested source SHA.
19. Destructive checkout/reset/clean operations are permitted only inside
    controlled Linux acceptance workspaces, never arbitrary developer
    worktrees.

Platform-specific adapters are permitted only when required by the operating
system. They must implement the same shared contracts and preserve observable
authentication, gameplay, packet, and database semantics.

## Linux issue classification

When Linux verification fails, first classify the failure using evidence:

- A deployment-only issue may be corrected in Linux packaging, configuration,
  service management, permissions, monitoring, or filesystem layout.
- A compatibility defect requiring source code must be repaired in the
  authoritative Windows source tree, validated on Windows, rebuilt for Linux,
  and verified again on Linux.
- A behavior difference in authentication, gameplay, packets, or database
  semantics is a cross-platform defect, not an acceptable Linux variation.

Production hot-fixes must not create an untracked Linux-only source fork. Any
emergency deployment change must be represented in the authoritative source
tree and pass the normal gates before it becomes the maintained solution.

## Guidance for AI agents

All AI assistants working on AORebirth must follow this policy. They must:

- implement product/runtime changes in the authoritative Windows source tree;
- validate on Windows using repository-approved workflows;
- promote only Windows-validated work to Linux;
- treat Linux checks as deployment and compatibility verification;
- keep platform-specific code narrow and contract-compatible;
- return product/runtime fixes to the Windows source tree first; keep permitted
  Linux infrastructure-only work in its owning private checkout.

AI assistants must not:

- create Linux-only implementations of core systems;
- treat Linux as the authoritative environment;
- introduce separate Windows and Linux behavior;
- replace or bypass the Windows development and acceptance workflow;
- maintain production-only source edits outside the authoritative repository.

## Change control

This is a permanent project-governance policy. Changes to it must be deliberate,
documentation-only governance changes approved by the project owner. Runtime or
deployment work must not silently weaken, reinterpret, or bypass it.
