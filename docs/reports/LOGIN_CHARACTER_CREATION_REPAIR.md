# Login character creation repair

Date: 2026-10-17

Base public master: `245cfaa41182f1e7c574f9d8a9fb4d6db8ebcf23`.
Branch: `codex/login-character-creation`.

## Proven defects and resulting behavior

Login's Windows build did not stage NewCharacter.json in its own executable-
relative GameData directory. Creation committed the bare character before
loading this data and writing stats. A subsequent failure could leave an
incomplete character, which the account list published with fabricated zero
stats. Lazy initialization also retained the first data-loading exception.

The repair stages NewCharacter.json for build and publish, preserves the existing
AO_REBIRTH_GAMEDATA_PATH selection, and logs Login's resolved GameData root.
Startup validates creation data before server initialization; invalid data logs
the path and exception. Headless initialization failure exits nonzero without
waiting for console input. Successful loads are cached; failed loads are not.

Account, selected breed/profession/gender, package, location and initial stat
definitions are validated before insertion. Existing editable content and
starting-vital calculations are preserved. The account list requires actual
stored Level, Breed, Sex and Profession rows and a positive playfield; Level,
Breed and Profession must be positive. Stored Sex zero remains valid. Skipping
an incomplete historical row logs its identity and missing field without
changing that row or hiding other valid characters.

Bitnykk's actual triggering exception and the reported GUI.dll crash cause
remain UNVERIFIED. These source defects are proven independently of that report.

## Transaction boundary

One existing database connection and transaction own:

1. Character insertion.
2. Initial statistics.
3. Starter item identity allocation, inventory and uploaded nanos.
4. Calculated current/maximum health and nano.
5. Starting coordinates and playfield identity.
6. The Rubi-Ka pending starting-area selection marker when applicable.

Existing DAOs receive that same connection/transaction. Borrowed persistence
operations do not begin, commit or dispose another transaction. Vitals read
uncommitted stats through that transaction. The creator commits once, then
returns the identity; only afterward can the handler send CharacterCreated.
Creation failure logs its stage and disconnects without a success message.

Pre-commit failures roll back all mandatory writes. Cleanup retains the primary
exception and attaches secondary failures. A lost commit acknowledgment or
post-commit cleanup failure retains the existing commit-outcome-unknown
classification and is never automatically retried. Auto-increment reservations
may leave harmless gaps; they are not persisted partial character state.

## Validation

- Approved Windows core build: PASS (36.30 seconds), with compiler/package
  warnings. Login, Chat and ZoneEngine_New outputs were built.
- Focused MSTest run against the actual LoginEngine assembly: 45 passed,
  zero failed, zero skipped (6.26 seconds including build).
- Existing disposable MySQL fixture: PASS (47.85 seconds including build).
  Actual creation and fresh DAO reloads cover all four breeds, authored stats,
  profession, location, vitals, inventory, nanos and RK/SL selection behavior.
- Injected real SQL failures after a stats insertion and after a mandatory
  selection-marker insertion: rollback PASS; complete persisted-data fingerprints
  unchanged; zero orphan rows; no creation commit.
- Missing account and invalid breed: rejected before opening a connection.
- Missing/malformed creation data, required fields, duplicate definitions,
  successful-load caching, recovery after a failed load, default/overridden
  GameData resolution and historical list filtering: PASS.
- Built Login GameData/NewCharacter.json matches the source SHA256:
  `37ae0bb59574d7b04f6a47915371c524f7b4a9c78a21527671e1d9a199d0e92c`.
  CopyToPublishDirectory is configured; a separate publish was not executed.
- Disposable fixture reports PRODUCTION_CONTACT=NO, ENGINES_STARTED=NO and
  complete container/network cleanup. No production character records or schema
  were changed. No backend engines were started or restarted for validation.
- Independent source review and git diff --check: PASS.

Commands:

```cmd
cmd /d /c tools\build_aorebirth_debug.cmd
cmd /d /c dotnet test AORebirth\Server\ZoneEngine_New.Tests\ZoneEngine_New.Tests.csproj --configuration Debug --nologo -p:LoginCharacterCreationFocusedTests=true
cmd /d /c Tools\run_zoneengine_schema_validation.cmd --run-character-creation
```

The focused test configurations use the existing full Login assembly instead of
Zone's reduced dependencies. Default test configurations exclude these focused
Login files and retain their original dependencies. The disposable mode uses
the existing guarded fixture and accepts no application connection or engine
arguments. Initial compile/test-wiring mistakes were corrected before the final
passing runs; no production behavior was changed to satisfy a stale fixture.

## Exact files changed

Runtime and staging:

```text
AORebirth/Libraries/Source/AORebirth.Database/Dao/NewCharacterStartAreaSelectionDao.cs
AORebirth/Libraries/Source/AORebirth.Database/Dao/StatDao.cs
AORebirth/Libraries/Source/AORebirth.Database/DatabaseDaoFactory.cs
AORebirth/Libraries/Source/AORebirth.Database/Domain/Characters/MySqlCharacterPersistenceDao.cs
AORebirth/Libraries/Source/AORebirth.Database/Domain/Missions/MySqlMissionDao.cs
AORebirth/Server/LoginEngine/CharacterCreation/CharacterStartPackageApplier.cs
AORebirth/Server/LoginEngine/CharacterCreation/NewCharacterStats.cs
AORebirth/Server/LoginEngine/CharacterCreation/StarterVitalStats.cs
AORebirth/Server/LoginEngine/MessageHandlers/CreateCharacterHandler.cs
AORebirth/Server/LoginEngine/Packets/CharacterName.cs
AORebirth/Server/LoginEngine/Program.cs
AORebirth/Server/LoginEngine/Properties/AssemblyInfo.cs
AORebirth/Server/LoginEngine/QueryBase/CharacterList.cs
WindowsBuildNet10/Projects/LoginEngine.WinNet10.csproj
```

Tests and documentation:

```text
AORebirth/Server/ZoneEngine_New.Tests/CharacterCreationTests.cs
AORebirth/Server/ZoneEngine_New.Tests/CharacterListTests.cs
AORebirth/Server/ZoneEngine_New.Tests/ZoneEngine_New.Tests.csproj
Tools/ZoneEngineSchemaValidation/CharacterCreationSmoke.cs
Tools/ZoneEngineSchemaValidation/Program.cs
Tools/ZoneEngineSchemaValidation/ZoneEngineSchemaValidation.csproj
Tools/run_zoneengine_schema_validation.cmd
docs/ai/CURRENT_TASK.md
docs/ai/WORKFLOW.md
docs/project/PROJECT_STATE.md
docs/reports/LOGIN_CHARACTER_CREATION_REPAIR.md
```

No GameData content or schema definition changed. Historical incomplete records
are retained. Client verification, Linux port/deployment and complete default
regression suites were not performed. Commit/disposal-fault handling was source
reviewed; creation-specific tests inject failures before commit, not an ambiguous
commit acknowledgment. No AO client was launched or controlled.
