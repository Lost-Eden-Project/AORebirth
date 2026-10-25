# AORebirth Build and Release Contract

Status: Permanent project policy. Apply this contract to every build and release
attempt. It supersedes older workflow descriptions that allow unaccepted Linux
release work, separate Linux content reconstruction or moving accepted inputs.
Repository `AGENTS.md` remains the agent-behavior authority.

## Authorities and roles

| Role | Authority and permitted responsibility |
| --- | --- |
| Public product source | `https://github.com/subarumike/AORebirth`, branch `master`, is the only product/runtime source authority. |
| `PUBLIC_PRODUCT_CHECKOUT` | The configured local Windows integration/build checkout; synchronize it from public master before each Windows build. Local edits do not create a competing product authority. |
| `PRIVATE_CONTENT_UPSTREAM` | The configured private upstream authority for private GameData/content changes. Its exact repository binding stays in private governance. |
| `CANONICAL_WINDOWS_GAMEDATA` | The reconciled private dataset used by Windows, including required coexisting private/generated content and canonical repairs. A cached tree is not upstream authority. |
| `PRIVATE_LINUX_CHECKOUT` / `PRIVATE_LINUX_REPOSITORY` | The private derived source, allowlisted Linux build/packaging/service adaptations, tooling and release provenance. They do not own independent product behavior or content. |
| `ONLINE_LINUX_BUILDER` | The existing private GitHub Actions builder for Linux binaries and deployable live-server packages. |

Private repository bindings, infrastructure, credentials, protected GameData,
build artifacts and release receipts remain private. Public documentation uses
the role names above instead of publishing those private details. Existing
deployment-tooling access and publication rules remain in force.

## Windows preparation and acceptance

1. Fetch public master and record the exact `PUBLIC_MASTER_SHA`.
2. Synchronize the Windows checkout to that SHA, preserving unrelated work.
   Before compiling, require `LOCAL_WINDOWS_SOURCE_SHA == PUBLIC_MASTER_SHA`.
   Uncommitted product/runtime changes are not an accepted substitute.
3. Fetch/check `PRIVATE_CONTENT_UPSTREAM` and record `CONTENT_DUMP_SHA` on every
   build/release attempt. Missing access or an unknown current upstream SHA
   fails closed, even when a cached content tree exists.
4. Compare the fetched dump content and SHA with the recorded basis of canonical
   Windows GameData. Reconcile every approved addition, modification and removal
   before building Windows. Apply upstream-owned removals against their recorded
   ownership/basis; do not prune unrelated private/generated content. Preserve
   legitimate coexisting content and canonical repairs. An unexplained conflict,
   stale replacement or unreviewed content delta blocks the build.
5. Produce the deterministic complete `WINDOWS_GAMEDATA_MANIFEST` using the
   shared inventory/hash algorithm. Cover the complete selected dataset, not
   only the files changed in this release. Record content reconciliation and
   source provenance without publishing protected content.
6. Build Windows locally from the exact public source and refreshed canonical
   GameData. Use the established build and startup/readiness workflows.
7. Stop after Windows build/startup readiness and hand the build to Mike for
   testing. Do not begin Linux release work or silently accept the build.
8. Record `WINDOWS_USER_ACCEPTED=YES` only after Mike explicitly accepts that
   exact Windows build. The acceptance receipt must bind `PUBLIC_MASTER_SHA`,
   `CONTENT_DUMP_SHA`, `WINDOWS_GAMEDATA_MANIFEST` and the exact Windows
   build/startup receipt hash. Preserve the explicit user-acceptance evidence.

Successful automated checks, an agent-authored acceptance flag, historical
approval and deployment authorization are not substitutes for Mike's explicit
Windows acceptance. Mike operates the AO client; build tooling does not launch
or control it without a separate explicit request.

## Freeze the accepted release

After acceptance, freeze the exact source/dump/manifest tuple and its hash-bound
acceptance receipt. Record `ACCEPTED_PUBLIC_MASTER_SHA` as the accepted public
source identity and preserve the distinct derived Linux/package source SHAs.

Public master and private content development may continue. Mandatory fresh
upstream checks record newly observed tips, but newer source or content belongs
to the next release. Do not silently move the accepted tuple or rebuild its
canonical data from a newer tip. Only Mike's explicit re-reconciliation request
starts a new Windows synchronization, content reconciliation, build/readiness and
user-acceptance cycle. Changed inputs invalidate the prior acceptance.

## Linux and live-server package

1. Validate the explicit Windows acceptance and frozen inputs before Linux
   release work. Reconcile Linux product source to the exact accepted public
   SHA. Inventory all differences and allow only explicit Linux
   build/packaging/service adaptations that preserve public product semantics.
2. Require `LINUX_PUBLIC_BASE_SHA == ACCEPTED_PUBLIC_MASTER_SHA` and
   `PUBLIC_RUNTIME_DIFFS=0`. Private derived commits do not make Linux an
   independent product authority; runtime equality is not waived by tooling or
   content allowances.
3. Supply the exact Windows-accepted canonical GameData. Do not reconstruct a
   separate Linux dataset or substitute a dump/cache/package for the accepted
   canonical tree.
4. Use the existing private GitHub Actions online builder only. Do not build
   Linux or live-server files locally through Docker/WSL, on production, or
   through an alternate builder. GitHub Actions produces the validated Linux
   binaries and deployable live-server package from the frozen inputs.
5. Produce `LINUX_GAMEDATA_MANIFEST` with the same complete deterministic
   algorithm and require equality with `WINDOWS_GAMEDATA_MANIFEST`.
6. Validate all required engine publishes, WorldEngine when present,
   Login/Zone/Chat, package integrity, startup/readiness, service integration,
   provenance and GameData completeness. Preserve the existing distinction
   between proven public baseline findings and new Linux failures.
7. Push validated Linux results only to the verified private Linux destination.
   Stop there unless deployment is explicitly authorized.

## Authorized deployment and live verification

Explicit Windows acceptance and build/package approval do not authorize a
deployment. When Mike separately authorizes deployment, use the existing normal
deployment workflow and its approved tooling-access exception.

Install the exact accepted online-built Linux binaries and the complete accepted
GameData delta: all additions, modifications and removals, together with required
governed migrations/configuration. Changed-file/incremental transfer is allowed
only when it yields the complete accepted result. A partial content subset is
not an accepted deployment.

After deployment, compute `LIVE_GAMEDATA_MANIFEST` from the complete actual
selected live dataset using the same deterministic algorithm. Require:

```text
LOCAL_WINDOWS_SOURCE_SHA == PUBLIC_MASTER_SHA
LINUX_PUBLIC_BASE_SHA == ACCEPTED_PUBLIC_MASTER_SHA
PUBLIC_RUNTIME_DIFFS=0
WINDOWS_ACCEPTED_GAMEDATA_MANIFEST == WINDOWS_GAMEDATA_MANIFEST
WINDOWS_ACCEPTED_GAMEDATA_MANIFEST == LINUX_GAMEDATA_MANIFEST == LIVE_GAMEDATA_MANIFEST
```

Keep the accepted inputs and resulting source/content manifests in private
provenance. An old package, deployment checkout or running release does not
redefine product/content authority. Mike performs final live client acceptance.

## Fail-closed validation

Refuse the affected build, Linux release or deployment when:

- public master cannot be fetched or Windows source does not match the current
  public-master SHA before its build;
- private content upstream is inaccessible, its current SHA is unknown, or its
  approved changes have not been reconciled before Windows build;
- required canonical private/generated content or a repair would be silently
  lost, or a content conflict/removal has no approved ownership decision;
- Mike has not explicitly accepted the exact Windows build, or acceptance
  evidence does not match the frozen source/dump/manifest/receipt hashes;
- Linux product source differs from the accepted public master or has an
  unexplained/unallowlisted adaptation;
- Linux consumes a different GameData manifest or an independently reconstructed
  dataset;
- GitHub Actions is bypassed for Linux/live-server binaries or package files;
- a deployment lacks explicit authorization or would omit an accepted content
  addition, modification or removal;
- the computed live manifest differs from the accepted Windows/Linux manifest.

Use the existing repository-owned gates and their regression checks. Never
manufacture a receipt, relabel historical evidence, suppress an unknown mismatch
or change product behavior merely to satisfy a release validator.
