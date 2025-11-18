# Contributing to Simple VS Manager

Thanks for wanting to help out with Simple VS Manager!

This is a small project, meant to stay friendly and simple for most users.  
You don’t need to be a Git expert to contribute. I'm not!

---

## Branches – workflow

- **`main`**
  - The “stable” branch.
  - This is what users should build from.
  - All pull requests go into `main`.

- **`devchaos`**
  - This is where I'll work from, along with our future robot overlords. Currently using Codex, Copilot and GPT 5.1 Thinking.
  - History here may be messy and rewritten.
  - **Please don’t base your work on `devchaos`**. Always start from `main`.

When pull requests are merged into `main`, I'll probably **squash** them into a single clean commit. At least the devchaos.

---

## How to contribute

### 1. Get the code

- If you don’t have write access:
  - Fork the repo on GitHub.
  - Clone your fork to your computer.
- If you do have access:
  - Clone the main repo if you haven’t already.

Make sure you’re up to date:

```bash
git checkout main
git pull
````

### 2. Create a branch

Create a new branch from `main` for your change:

```bash
git checkout -b feature/my-change
```

Name it something short that describes what you’re doing, like:

* `feature/preset-filters`
* `bugfix/crash-empty-modlist`

### 3. Make your changes

* Change the code.
* Make as many local commits as you like. Use agents, it's fun!
* Make sure the project builds (for example in Visual Studio 2022/2026).
* Try the basic things you touched to see that they still work.

### 4. Push and open a pull request

Push your branch:

```bash
git push -u origin feature/my-change
```

Then on GitHub:

1. Open a **Pull Request** from your branch into `main`.
2. Write a short description on what it is and/or why it's needed.

I’ll review it, maybe ask some questions, and then merge it

---

## Style and features

* The main goal is: **simple for the average user**.
* It’s fine to add **advanced or complex features** as long as:

  * They’re clearly marked as advanced, and/or
  * They’re hidden behind a toggle, setting, or advanced section.
* Try to keep:

  * Names clear and understandable.
  * Error messages helpful.
  * UI options not overwhelming by default.

---

## Bugs and ideas

If you have found a bug or have an idea:

* Open a GitHub issue.
* Include:

  * What you expected to happen
  * What actually happened
  * Steps to reproduce, if possible

---

## License

By contributing, you agree that your code is released under the same license as the rest of the project (see `LICENSE` in the repo).

---

Thanks again for helping Simple VS Manager improve!
Even small things like wording, layout tweaks, or minor bug fixes are welcome.
