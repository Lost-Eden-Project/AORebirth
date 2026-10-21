"""Validate the documented checkout exception without accessing deployment tooling."""

import argparse
from pathlib import Path


START = "<!-- deployment-tooling-access:start -->"
END = "<!-- deployment-tooling-access:end -->"
DOCUMENTS = ("AGENTS.md", "docs/project/DEVELOPMENT_AUTHORITY.md")
ALLOWED = {
    "READ_LAUNCHER",
    "READ_REQUIRED_SUPPORTING_DEPLOYMENT_FILES",
    "EXECUTE_APPROVED_DEPLOYMENT_WORKFLOW",
    "USE_EXISTING_AUTHORIZED_PRODUCTION_SSH",
}
DENIED = {
    "READ_OUTSIDE_APPROVED_TOOLING_ROOT",
    "EDIT_PRODUCT_RUNTIME_SOURCE_IN_TOOLING_CHECKOUT",
    "REBUILD_PRODUCT_SOURCE_IN_TOOLING_CHECKOUT",
    "MODIFY_UNRELATED_FILES_IN_TOOLING_CHECKOUT",
    "EDIT_DEPLOYMENT_TOOLING_WITHOUT_SEPARATE_TASK_AUTHORIZATION",
    "TREAT_TOOLING_CHECKOUT_AS_PRODUCT_AUTHORITY",
    "PUBLISH_PRIVATE_LINUX_MATERIAL_PUBLICLY",
}


def read_contract(path):
    text = path.read_text(encoding="utf-8-sig")
    assert text.count(START) == text.count(END) == 1, path
    section = text.split(START, 1)[1].split(END, 1)[0]
    prose = " ".join(section.split())
    for requirement in (
        "Explicit deployment authorization itself is sufficient authorization to use "
        "APPROVED_DEPLOYMENT_TOOLING_ROOT; no additional checkout-root waiver is required.",
        "Never silently edit product/runtime source in another checkout.",
        "Exact private role bindings stay in private governance, never in public commits.",
    ):
        assert requirement in prose, path
    policy = {}
    for line in section.splitlines():
        if not line.startswith("| "):
            continue
        cells = tuple(cell.strip() for cell in line.strip("|").split("|"))
        if cells[0] in {"Action", "---"}:
            continue
        assert len(cells) == 3 and cells[0] not in policy, path
        policy[cells[0]] = cells[1:]
    return section, policy


def validate_contract(policy):
    assert set(policy) == ALLOWED | DENIED, "Missing or unexpected access action"
    for action in ALLOWED:
        assert policy[action] == ("ALLOW", "DENY"), action
    for action in DENIED:
        assert policy[action] == ("DENY", "DENY"), action


def validate_root(root):
    contracts = [read_contract(root / name) for name in DOCUMENTS]
    assert contracts[0][0] == contracts[1][0], "Governing documents disagree"
    for _, policy in contracts:
        validate_contract(policy)
        # Prove both authorization regressions and an out-of-scope grant fail.
        action = sorted(ALLOWED)[0]
        for changed_action, verdict in (
            (action, ("DENY", "DENY")),
            (action, ("ALLOW", "ALLOW")),
            ("READ_OUTSIDE_APPROVED_TOOLING_ROOT", ("ALLOW", "DENY")),
        ):
            corrupted = {**policy, changed_action: verdict}
            try:
                validate_contract(corrupted)
            except AssertionError:
                continue
            raise AssertionError("Regression was not detected: " + changed_action)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", action="append", type=Path,
                        help="governance checkout to validate; repeat for additional checkouts")
    args = parser.parse_args()
    for root in args.root or [Path(__file__).resolve().parents[2]]:
        validate_root(root)
    print("EXPLICIT_DEPLOYMENT_AUTHORIZATION -> DEPLOYMENT_TOOLING_ACCESS_ALLOWED=PASS")
    print("NO_DEPLOYMENT_AUTHORIZATION -> DEPLOYMENT_TOOLING_ACCESS_DENIED=PASS")
    print("PRODUCT_AND_PRIVATE_BOUNDARIES=PASS")


if __name__ == "__main__":
    main()
