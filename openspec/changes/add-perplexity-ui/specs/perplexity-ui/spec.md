# Perplexity UI Specification

## Purpose

Define desktop behavior required to expose Perplexity WebUI Scraper as first-class engine in Tunnel Agent without regressing existing CLIProxyAPI workflow.

## Requirements

### Requirement: Active engine selection

The system MUST let user choose which managed engine the desktop app is currently focusing and configuring through Providers sidebar submenus, without implying that other managed engines are stopped.

#### Scenario: User switches from CLIProxyAPI to Perplexity

- GIVEN CLIProxyAPI is currently selected in desktop app
- WHEN user selects Perplexity WebUI Scraper from Providers sidebar submenu
- THEN app SHALL update visible status, actions, and engine details to reference Perplexity
- AND app SHALL preserve CLIProxyAPI provider/account data without deleting it
- AND app SHALL use Perplexity engine runtime settings for endpoint and engine operations shown in UI
- AND app SHALL NOT stop CLIProxyAPI solely because UI focus changed

#### Scenario: User returns to CLIProxyAPI

- GIVEN Perplexity is currently selected in desktop app
- WHEN user selects CLIProxyAPI from Providers sidebar submenu
- THEN app SHALL restore provider-management workflow for OAuth and custom providers
- AND app SHALL show CLIProxyAPI endpoint, install state, and configuration copy again
- AND app SHALL NOT stop Perplexity solely because UI focus changed

### Requirement: Perplexity account management

The system MUST provide dedicated UI for stored Perplexity accounts required by scraper engine.

#### Scenario: No Perplexity accounts configured

- GIVEN active engine is Perplexity
- AND no Perplexity accounts are stored
- WHEN user opens Perplexity management surface
- THEN app SHALL show empty-state guidance that Perplexity requires at least one saved session account
- AND app SHALL offer primary action to add account

#### Scenario: User adds Perplexity account

- GIVEN active engine is Perplexity
- WHEN user submits account label and session token
- THEN app SHALL persist new account
- AND app SHALL show account in Perplexity account list
- AND app SHALL mark first saved account as default if no default existed before

#### Scenario: User changes default account

- GIVEN active engine is Perplexity
- AND multiple Perplexity accounts are stored
- WHEN user marks one account as default
- THEN app SHALL persist that account as default
- AND app SHALL clear default marker from previously default account
- AND app SHALL reflect default state in account list without requiring app restart

#### Scenario: User removes account

- GIVEN active engine is Perplexity
- AND at least one Perplexity account is stored
- WHEN user removes one account
- THEN app SHALL delete that account from persisted settings
- AND app SHALL update account list immediately
- AND if removed account was default and another account remains, app SHALL show one remaining account as default

### Requirement: Engine-specific status and operations

The system MUST scope focused status, install/update controls, version details, endpoint information, and engine-specific configuration sections to selected engine while allowing managed engines to run in parallel.

#### Scenario: User inspects active engine status

- GIVEN desktop app has selected active engine
- WHEN status and configuration surfaces render
- THEN engine name, version labels, install/update actions, status text, and endpoint SHALL describe selected engine
- AND controls SHALL operate on selected engine only

#### Scenario: User starts selected engine

- GIVEN selected engine is installed and configured enough to run
- WHEN user starts engine from UI
- THEN app SHALL start selected engine implementation
- AND status surface SHALL transition through startup state into running or error state for that engine
- AND app SHALL leave any other already-running managed engine unchanged unless user explicitly stops it

#### Scenario: Multiple engines run simultaneously

- GIVEN CLIProxyAPI and Perplexity are both running
- WHEN user changes focused engine from Providers sidebar submenu
- THEN app SHALL update primary controls and details to selected engine
- AND app SHALL continue showing correct running state for selected engine
- AND app SHALL avoid presenting focus switch as global engine shutdown or takeover

#### Scenario: Perplexity shows dedicated configuration section

- GIVEN Perplexity is focused in desktop app
- WHEN user opens configuration surface
- THEN app SHALL show Perplexity-specific engine configuration section parallel to CLIProxyAPI engine configuration structure
- AND section SHALL include Perplexity engine controls relevant to versioning, installation, status, port, and account/session guidance
- AND section SHALL NOT show routing strategy control inside Perplexity-specific section

### Requirement: Perplexity-specific content isolation

The system MUST avoid mixing CLIProxyAPI provider-management controls into Perplexity mode when those controls are not relevant.

#### Scenario: Perplexity mode hides unrelated provider controls

- GIVEN active engine is Perplexity
- WHEN user opens Perplexity submenu under Providers
- THEN app SHALL show Perplexity-specific account and engine guidance instead of OAuth/custom provider catalog
- AND app SHALL not require user to interpret disabled CLIProxyAPI provider controls to use Perplexity

#### Scenario: CLIProxyAPI mode keeps existing provider workflow

- GIVEN active engine is CLIProxyAPI
- WHEN user opens CLIProxyAPI submenu under Providers
- THEN app SHALL continue showing existing provider catalog, account expansion, and provider toggles
- AND Perplexity-specific account editor SHALL not replace that workflow
