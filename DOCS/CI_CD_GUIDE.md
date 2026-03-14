# ORBIT CI/CD Guide

## What is CI/CD?

**CI/CD** stands for **Continuous Integration** and **Continuous Delivery/Deployment**. It is a method to frequently deliver apps to customers by introducing automation into the stages of app development.

*   **Continuous Integration (CI):** This practice involves developers making frequent changes to their code and verifying those changes by automatically building the application and running automated tests. The goal is to detect errors ("integration bugs") as quickly as possible.
*   **Continuous Delivery (CD):** This automated build is then ready to be deployed to a test or production environment.

In the context of **ORBIT**, we are primarily focusing on **Continuous Integration** right now: ensuring that every time you save changes (push code to GitHub), the project still builds and runs correctly on all supported platforms (Windows, macOS, Linux).

## How It Works: GitHub Actions

We use **GitHub Actions**, a platform built directly into GitHub, to run our CI pipeline.

### The Workflow File: `build.yml`

I have created a configuration file at `.github/workflows/build.yml`. This file tells GitHub exactly what to do. Here is a breakdown of its components:

1.  **Triggers (`on`):**
    *   **`push`**: Runs whenever you push code to the `main` or `master` branch.
    *   **`pull_request`**: Runs whenever someone opens a Pull Request (PR) proposing changes.
    *   **`workflow_dispatch`**: Allows you to run the build manually from the GitHub UI (actions tab).

2.  **The Job (`jobs` & `build`):**
    *   This defines a job named "Build on [OS]".

3.  **The Matrix (`strategy matrix`):**
    *   `os: [windows-latest, ubuntu-latest, macos-latest]`
    *   **This is the magic.** It tells GitHub to spin up **three separate virtual machines** simultaneously. The entire build process will run three times in parallel, once for each operating system. This ensures that a change made on Windows doesn't accidentally break the app on Mac or Linux.

4.  **The Steps (`steps`):**
    *   **Checkout**: Downloads your code to the virtual machine.
    *   **Setup .NET**: Installs the .NET 8.0 SDK so we can use `dotnet` commands.
    *   **Restore**: Runs `dotnet restore` to download all the libraries (NuGet packages) your project depends on.
    *   **Build**: Runs `dotnet build -c Release` to verify the code compiles without errors.
    *   **Test**: Runs `dotnet test` to execute any automated unit tests.

## How to Use It

### 1. Just Push Code
You don't need to do anything special! Just commit your changes and push them to GitHub as usual:

```bash
git add .
git commit -m "Added a cool new feature"
git push origin main
```

### 2. Check the Status
Once you push, go to your repository page on GitHub.

*   **On the main page:** You will see a small yellow dot (running), green checkmark (success), or red X (failure) next to your latest commit hash or at the top of the file list.
*   **Visualizing Progress:** Click on the **"Actions"** tab at the top of the repo.
    *   You will see a list of "Workflow runs".
    *   Click on the latest run to see the details.
    *   You will see three boxes representing the three jobs (e.g., `Build on windows-latest`, `Build on ubuntu-latest`). Click on any of them to watch the console output in real-time!

### 3. Handling Failures
If the build fails (Red X):

1.  Click the failed run in the **Actions** tab.
2.  Click the specific job that failed (e.g., `Build on ubuntu-latest`).
3.  Scroll through the log to find the error message (it usually looks like the compiler errors you see in your local terminal).
4.  Fix the issue locally, commit, and push again. The process will restart automatically.

## Why is this important?

*   **Safety Net:** You can code with confidence knowing that if you break something, the robot will tell you immediately.
*   **Collaboration:** If others contribute to ORBIT, the CI ensures their code meets your standards before it's merged.
*   **Professionalism:** It shows users that the project is healthy, stable, and actively maintaining quality standards.
