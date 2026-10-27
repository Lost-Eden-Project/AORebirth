"""Validate release-mode policy text only; never exercise the release pipeline."""

import argparse
from pathlib import Path


START = "<!-- patch-release-policy:start -->"
END = "<!-- patch-release-policy:end -->"
DEFAULT_CONTRACT = "docs/project/BUILD_RELEASE_CONTRACT.md"
FAILURE_POLICY = {
    "NEW_FAILURE": "FAIL",
    "CHANGED_BASELINE_FAILURE": "FAIL",
    "FAILURE_IN_PATCH_TOUCHED_CODE_OR_DATA": "FAIL",
    "REQUIRED_RELEASE_CONTRACT_FAILURE": "FAIL",
    "UNCHANGED_UNRELATED_BASELINE_FAILURE": "RECORD_AND_CONTINUE",
}
REQUIREMENTS = (
    "PATCH MODE is the default for routine AORebirth patches.",
    "It does not change product authority, input identity, Windows acceptance, privacy or deployment authorization.",
    "FULL RELEASE MODE is permitted only when Mike explicitly requests it or a material architecture, schema, "
    "build-system, engine-topology or other release-wide compatibility change actually requires it.",
    "Record the changed paths, material effect and necessary broader work before escalation.",
    "Unrelated inherited failures, a new source SHA, a receipt-format issue or a missing incremental-tooling feature "
    "never silently escalates PATCH MODE.",
    "Fetch and synchronize current public Windows master; require LOCAL_WINDOWS_SOURCE_SHA == PUBLIC_MASTER_SHA.",
    "Check the configured PRIVATE_CONTENT_UPSTREAM on every build.",
    "Reconcile only actual approved GameData additions, modifications and removals; preserve legitimate private "
    "runtime repairs and generated runtime content.",
    "Build/test Windows first within the changed dependency/subsystem scope.",
    "Existing explicit authorization requirements for automated suites and client control still apply.",
    "Stop for Mike's exact-input Windows acceptance.",
    "Freeze the accepted source SHA, content-dump SHA, GameData manifest and build/startup receipt. "
    "Never reuse acceptance for a changed tuple.",
    "Use dependency analysis and incremental detection in the existing private GitHub Actions online builder "
    "to build only impacted Linux engines/projects and transitive dependencies. Reuse verified unchanged engine outputs.",
    "Produce a delta package containing changed binaries/dependencies, changed or added GameData, and an explicit "
    "GameData removal list (empty when no removals).",
    "Do not download/reconstruct an entire previous release merely to create a patch.",
    "With explicit deployment authorization, deploy that immutable delta; validate the actual-database backup, "
    "required migration state, affected service readiness and dependent health, provenance, package integrity "
    "and final manifest match.",
    "No local Docker/WSL or production Linux build is an alternative.",
    "Derive the target deterministically from a verified base plus the accepted delta and removal list; "
    "reuse validated unchanged entries/assets.",
    "Require Windows == Linux == live complete GameData manifests.",
    "Never preserve an unexplained Linux-only overlay, omit a removal, prune unrelated private content "
    "or substitute an unchecked cache for authority.",
    "Record a known baseline failure once with its subsystem/input fingerprint, failure signature and evidence. "
    "Reuse that record when the subsystem and relevant inputs are unchanged.",
    "Do not rerun unrelated inherited failures, investigate them as new regressions, or use them to justify "
    "broad gameplay/cave/client suites.",
    "Declare an explicit test/check timeout before execution, using the check's expected workload.",
    "Silence is not a hang. Do not interrupt a quiet test before its declared timeout expires.",
    "On expiry retain diagnostics and stop only the owned process.",
    "Broad suites require patch relevance and existing authorization; FULL RELEASE MODE does not erase "
    "baseline classifications or timeout discipline.",
    "Canonical GameData admits only explicitly identified runtime-consumed content: loader/configuration/"
    "schema-backed paths and reviewed generated runtime outputs.",
    "An extension such as JSON alone is not proof of runtime use.",
    "Cache, research output, reports, documentation/README files, screenshots, temporary exports, build artifacts, "
    "logs, backups and other non-runtime material must fail admission or be deterministically excluded "
    "BEFORE manifest/package generation.",
    "Unknown or unclassified paths fail closed; reject path/symlink escapes.",
    "Excluded material stays outside canonical GameData.",
    "Do not silently delete unique evidence or rewrite an accepted package; any cleanup delta uses normal "
    "governed ownership, review and an explicit removal list.",
    "Use available GitHub Actions caching for package restore, build intermediates, unchanged private assets "
    "and unchanged GameData.",
    "Cache keys/provenance must bind relevant source/dependency/content identities and reject stale or corrupt reuse.",
    "Automatically derive impact, manifests, source audits and receipt metadata from verified inputs and actual execution.",
    "Do not stage policy/receipt files manually outside the governed pipeline or fabricate approvals, hashes, "
    "measurements or acceptance.",
    "Fresh upstream checks, source parity and trust-boundary validation remain mandatory even on a cache hit.",
    "Record phase timings and cache decisions automatically; reuse unchanged verified evidence instead of "
    "hour-long manual release investigations.",
    "These are governance requirements, not a claim that every existing wrapper already implements selective "
    "execution, caching or runtime-content filtering.",
    "If an entrypoint cannot satisfy PATCH MODE, identify its exact limitation and the smallest required tooling "
    "correction; do not bypass a release gate, invent unsupported switches, silently perform heavyweight work "
    "or relabel it FULL.",
)


def policy_document(root, relative):
    path = Path(relative)
    assert not path.is_absolute() and ".." not in path.parts, "Policy path must be checkout-relative"
    target = (root / path).resolve(strict=True)
    assert target.is_relative_to(root) and target.is_file(), "Policy path escapes its checkout"
    return target


def read_contract(path):
    text = path.read_text(encoding="utf-8-sig")
    assert text.count(START) == text.count(END) == 1, (path, "Missing or duplicated policy markers")
    assert text.index(START) < text.index(END), (path, "Policy markers are reversed")
    return text.split(START, 1)[1].split(END, 1)[0]


def validate_safeguards(section):
    prose = " ".join(section.split())
    for requirement in REQUIREMENTS:
        assert requirement in prose, "Missing release safeguard: " + requirement


def validate_contract(section):
    validate_safeguards(section)
    policy = {}
    for line in section.splitlines():
        if not line.startswith("| "):
            continue
        cells = tuple(cell.strip() for cell in line.strip("|").split("|"))
        if cells[0] in {"Failure condition", "---"}:
            continue
        assert len(cells) == 2 and cells[0] not in policy, "Invalid or duplicate failure policy row"
        policy[cells[0]] = cells[1]
    assert policy == FAILURE_POLICY, "Failure policy permits an unsafe escalation or hides a regression"


def rejects(section, validator=validate_contract):
    try:
        validator(section)
    except AssertionError:
        return
    raise AssertionError("A weakened release policy was not detected")


def validate_root(root, contract):
    root = root.resolve(strict=True)
    sections = [read_contract(policy_document(root, name)) for name in ("AGENTS.md", contract)]
    assert sections[0] == sections[1], "AGENTS.md and release contract disagree"
    for section in sections:
        validate_contract(section)
    # Check each required safeguard and every failure classification independently.
    normalized = " ".join(sections[0].split())
    for requirement in REQUIREMENTS:
        rejects(normalized.replace(requirement, "REMOVED_POLICY_REQUIREMENT", 1), validate_safeguards)
    for condition, verdict in FAILURE_POLICY.items():
        replacement = "PASS" if verdict == "FAIL" else "FAIL"
        rejects(sections[0].replace("| " + condition + " | " + verdict + " |",
                                    "| " + condition + " | " + replacement + " |", 1))
    rejects(sections[0] + "\n| UNREVIEWED_CONDITION | RECORD_AND_CONTINUE |\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository-root", type=Path, default=Path(__file__).resolve().parents[2],
                        help="governance checkout to read; defaults to this checker's checkout")
    parser.add_argument("--contract", default=DEFAULT_CONTRACT,
                        help="checkout-relative release contract; private copies may use their existing document")
    args = parser.parse_args()
    validate_root(args.repository_root, args.contract)
    print("PATCH_MODE_DEFAULT_AND_SYNCHRONIZED=PASS")
    print("SOURCE_ACCEPTANCE_ONLINE_AND_DEPLOYMENT_SAFEGUARDS=PASS")
    print("FAILURE_BASELINE_TIMEOUT_CONTENT_AND_CACHE_POLICY=PASS")
    print("POLICY_REGRESSION_DETECTION=PASS")
    print("VALIDATION_SCOPE=POLICY_TEXT_ONLY; PIPELINE_IMPLEMENTATION_NOT_EXERCISED")


if __name__ == "__main__":
    main()
