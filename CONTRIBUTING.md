# Contributing to ORBIT

First off, thanks for taking the time to contribute! 🎉

The following is a set of guidelines for contributing to ORBIT. These are mostly guidelines, not rules. Use your best judgment, and feel free to propose changes to this document in a pull request.

## Code of Conduct

This project and everyone participating in it is governed by the [ORBIT Code of Conduct](CODE_OF_CONDUCT.md). By participating, you are expected to uphold this code.

## How Can I Contribute?

### Reporting Bugs

This section guides you through submitting a bug report for ORBIT. Following these guidelines helps maintainers and the community understand your report, reproduce the behavior, and find related reports.

- **Use a clear and descriptive title.**
- **Describe the exact steps to reproduce the problem.**
- **Provide specific examples to demonstrate the steps.**
- **Describe the behavior you observed after following the steps.**
- **Include logs and screenshots.**

### Suggesting Enhancements

This section guides you through submitting an enhancement suggestion for ORBIT, including completely new features and minor improvements to existing functionality.

- **Use a clear and descriptive title.**
- **Provide a step-by-step description of the suggested enhancement.**
- **Provide specific examples to demonstrate the need.**
- **Describe the current behavior and explain which behavior you expected to see instead.**

## Development Workflow

1.  **Fork the repo** and create your branch from `main`.
2.  **Dependencies**: Ensure you have .NET 8.0 SDK installed.
3.  **Build**: Run `dotnet build` to verify everything works.
4.  **Test**: Run `dotnet test` to ensure no regressions.
5.  **Commit**: Make sure your commit messages are descriptive.
6.  **Push** to your fork and submit a Pull Request.

### Coding Standards

- **Naming**: Use PascalCase for public members changes, camelCase for private fields (with `_` prefix).
- **Documentation**: Use XML documentation `///` for all public APIs.
- **Async**: Use `async/await` appropriately. Avoid `.Result` or `.Wait()`.
- **Formatting**: The project uses standard C# formatting rules.

## Pull Request Process

1.  Update the `README.md` with details of changes to the interface, this includes new environment variables, exposed ports, useful file locations and container parameters.
2.  Increase the version numbers in any examples files and the README.md to the new version that this Pull Request would represent.
3.  You may merge the Pull Request in once you have the sign-off of two other developers, or if you do not have permission to do that, you may request the second reviewer to merge it for you.
