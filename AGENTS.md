# Coding agent guidance

## Repository map

- [README.md](README.md) and [docs/](docs/) describe the Windows 365 for Agents platform.
- [W365A-Playground-Agent/](W365A-Playground-Agent/) contains the runnable C# / .NET 10 sample.
- [.config/dotnet-tools.json](.config/dotnet-tools.json) pins the optional Agent 365 CLI used for agent setup and packaging.

Before working in the playground, read
[W365A-Playground-Agent/AGENTS.md](W365A-Playground-Agent/AGENTS.md).
It contains the sample's build/test commands, architecture, and configuration boundaries.
Do not apply sample-specific requirements to unrelated platform documentation.

## Working in a fork

- Follow [CONTRIBUTING.md](CONTRIBUTING.md) when preparing an upstream contribution.
- Treat the playground's fork-and-customize guide as a human-led coding-agent workflow: the developer sets the goal, reviews changes, and approves live operations.
- Keep shared documentation and instructions usable by a fresh fork: use relative links and placeholders, not personal paths, accounts, credentials, or deployment names.
- Keep changes focused on the requested customization. Update directly affected documentation and distinguish verified checks from checks requiring live services.
- Preserve the existing licenses: playground code is MIT; platform documentation and other non-code assets are CC-BY-4.0. See the [root license](LICENSE.md) and [sample license](W365A-Playground-Agent/LICENSE).
