# 🤝 Contributing to ORBIT-Pure

Welcome! We're excited that you're interested in contributing to ORBIT-Pure. This document provides guidelines and information for contributors.

## 📋 Table of Contents
- [Code of Conduct](#code-of-conduct)
- [Getting Started](#getting-started)
- [Development Setup](#development-setup)
- [Contributing Guidelines](#contributing-guidelines)
- [Reporting Issues](#reporting-issues)
- [Pull Request Process](#pull-request-process)
- [Development Workflow](#development-workflow)

---

## 📜 Code of Conduct

This project follows a code of conduct to ensure a welcoming environment for all contributors. By participating, you agree to:

- **Be respectful** and inclusive in all interactions
- **Focus on constructive feedback** and collaborative problem-solving
- **Respect differing viewpoints** and experiences
- **Show empathy** towards other community members
- **Use inclusive language** and avoid discriminatory content

---

## 🚀 Getting Started

### Prerequisites
- **.NET 9.0 SDK** - Download from [Microsoft](https://dotnet.microsoft.com/download)
- **Git** - Version control system
- **Visual Studio 2022** or **VS Code** with C# extensions
- **SQLite** - Included with .NET, no separate installation needed

### Quick Setup
```bash
# Clone the repository
git clone https://github.com/MeshDigital/Orbit-pure.git
cd Orbit-pure

# Restore dependencies
dotnet restore

# Build the project
dotnet build

# Run the application
dotnet run
```

---

## 🛠️ Development Setup

### Environment Configuration
1. **Clone the repository** with submodules if needed
2. **Install dependencies** using `dotnet restore`
3. **Configure development settings** in `appsettings.Development.json`
4. **Set up Soulseek credentials** for testing P2P features

### Recommended Tools
- **Visual Studio 2022** (Windows) or **VS Code** (Cross-platform)
- **GitHub Desktop** or **GitKraken** for Git management
- **Postman** or **Insomnia** for API testing
- **SQLite Browser** for database inspection

### Project Structure
```
ORBIT-Pure/
├── Views/           # Avalonia UI components
├── ViewModels/      # MVVM view models
├── Services/        # Business logic and integrations
├── Models/          # Data models and DTOs
├── Data/            # Entity Framework and database
├── Converters/      # XAML value converters
├── Helpers/         # Utility classes
└── Tests/           # Unit and integration tests
```

---

## 📝 Contributing Guidelines

### Code Style
- **C# Coding Standards**: Follow Microsoft's C# coding conventions
- **Naming**: Use PascalCase for classes/properties, camelCase for variables
- **Documentation**: XML documentation comments for public APIs
- **Async/Await**: Use async/await pattern for asynchronous operations
- **Dependency Injection**: Register services in the DI container

### Commit Messages
Use clear, descriptive commit messages following this format:
```
type(scope): description

[optional body]

[optional footer]
```

Types:
- `feat`: New features
- `fix`: Bug fixes
- `docs`: Documentation changes
- `style`: Code style changes
- `refactor`: Code refactoring
- `test`: Testing related changes
- `chore`: Maintenance tasks

Examples:
```
feat(audio): add spectral analysis for transcoding detection
fix(downloads): resolve connection timeout in poor network conditions
docs(readme): update installation instructions for .NET 9.0
```

### Branch Naming
- `feature/description`: New features
- `bugfix/issue-description`: Bug fixes
- `hotfix/critical-fix`: Critical production fixes
- `docs/update-documentation`: Documentation updates

---

## 🐛 Reporting Issues

### Bug Reports
When reporting bugs, please include:

1. **Clear Title**: Describe the issue concisely
2. **Environment**: OS, .NET version, application version
3. **Steps to Reproduce**: Numbered steps to reproduce the issue
4. **Expected Behavior**: What should happen
5. **Actual Behavior**: What actually happens
6. **Screenshots/Logs**: Visual evidence or error logs
7. **Additional Context**: Any relevant information

### Feature Requests
For new features, please provide:

1. **Use Case**: Describe the problem this feature would solve
2. **Proposed Solution**: How you envision the feature working
3. **Alternatives**: Other approaches you've considered
4. **Priority**: Why this feature is important
5. **Mockups**: Visual representations if applicable

### Security Issues
- **DO NOT** report security vulnerabilities publicly
- Email security concerns to: [security@orbit-pure.dev](mailto:security@orbit-pure.dev)
- Include detailed reproduction steps and potential impact

---

## 🔄 Pull Request Process

### Before Submitting
1. **Update Documentation**: Ensure README, docs, and code comments are updated
2. **Write Tests**: Add unit tests for new functionality
3. **Code Review**: Self-review your code before submitting
4. **Branch Status**: Ensure your branch is up-to-date with main
5. **Clean Commits**: Squash related commits and write clear messages

### PR Template
Please use this template when creating pull requests:

```markdown
## Description
Brief description of the changes made.

## Type of Change
- [ ] Bug fix (non-breaking change)
- [ ] New feature (non-breaking change)
- [ ] Breaking change
- [ ] Documentation update
- [ ] Refactoring

## Testing
- [ ] Unit tests added/updated
- [ ] Integration tests added/updated
- [ ] Manual testing completed
- [ ] All tests pass

## Screenshots (if applicable)
Add screenshots to help explain your changes.

## Checklist
- [ ] My code follows the project's style guidelines
- [ ] I have performed a self-review of my own code
- [ ] I have commented my code, particularly in hard-to-understand areas
- [ ] My changes generate no new warnings
- [ ] I have added tests that prove my fix/feature works
- [ ] New and existing unit tests pass locally
```

### Review Process
1. **Automated Checks**: CI/CD pipeline runs tests and builds
2. **Code Review**: At least one maintainer reviews the changes
3. **Feedback**: Address any review comments or requested changes
4. **Approval**: Maintainers approve the PR
5. **Merge**: PR is merged using squash or rebase strategy

---

## 🔄 Development Workflow

### Daily Development
1. **Pull Latest**: `git pull origin main` to get latest changes
2. **Create Branch**: `git checkout -b feature/your-feature-name`
3. **Make Changes**: Implement your feature or fix
4. **Test Locally**: Run tests and manual verification
5. **Commit Changes**: `git commit -m "feat: description of changes"`
6. **Push Branch**: `git push origin feature/your-feature-name`
7. **Create PR**: Open a pull request on GitHub

### Testing Strategy
- **Unit Tests**: Test individual components and methods
- **Integration Tests**: Test service interactions and workflows
- **UI Tests**: Verify user interface functionality
- **Manual Testing**: Real-world usage verification
- **Beta Testing**: Community feedback collection

### Release Process
1. **Feature Complete**: All planned features implemented and tested
2. **Beta Testing**: Community testing and feedback collection
3. **Bug Fixes**: Address issues discovered during beta
4. **Release Candidate**: Final testing and validation
5. **Production Release**: Official release with documentation

---

## 🎯 Areas for Contribution

### High Priority
- **Audio Analysis**: Improve forensic detection algorithms
- **Performance**: Optimize large library operations
- **UI/UX**: Enhance user interface and experience
- **Testing**: Expand test coverage and reliability

### Medium Priority
- **Documentation**: Improve guides and API documentation
- **Internationalization**: Add support for multiple languages
- **Accessibility**: Improve screen reader and keyboard navigation
- **Mobile Support**: Investigate mobile platform compatibility

### Future Opportunities
- **Plugin System**: Create extension architecture
- **Cloud Integration**: Optional cloud backup and sync
- **Advanced AI**: Machine learning for better recommendations
- **Hardware Integration**: DJ controller and audio interface support

---

## 📞 Getting Help

### Communication Channels
- **GitHub Issues**: Bug reports and feature requests
- **GitHub Discussions**: General questions and community discussion
- **Documentation**: Check existing docs and guides first

### Response Times
- **Bug Reports**: Acknowledged within 24 hours
- **Feature Requests**: Initial response within 3 days
- **Pull Request Reviews**: Within 2-3 business days
- **General Questions**: Best effort basis

---

## 🙏 Recognition

Contributors are recognized in several ways:
- **GitHub Contributors**: Listed in repository contributors
- **Changelog**: Mentioned in release notes
- **Credits**: Special recognition for significant contributions
- **Community**: Access to contributor-only discussions

---

Thank you for contributing to ORBIT-Pure! Your efforts help make high-fidelity music management accessible to everyone.

*For questions or assistance, please open an issue on GitHub.*
2.  Increase the version numbers in any examples files and the README.md to the new version that this Pull Request would represent.
3.  You may merge the Pull Request in once you have the sign-off of two other developers, or if you do not have permission to do that, you may request the second reviewer to merge it for you.
