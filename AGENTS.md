# Repository instructions

- Never add files from `media/` to Git. Screenshots and recordings are local-only captures;
  they may include copyrighted game audiovisual content. Keep them ignored and out of commits.

## Server-authored abilities

- **Read `ABILITY_AUTHORING.md` before touching `statMods`, `statModAppears`, `buffs_set`,
  or any hero's `stat_mods` list.** It documents the working pipeline, the verified wire
  keys, and a list of settled dead ends. Several of those dead ends cost multiple test
  cycles each and are cheap to fall into again.
- The current objective is **generalized ability assignment** — any bot, any subset of
  abilities. Authoring another hardcoded kit is not progress toward it; see
  `ABILITY_AUTHORING.md` §5 for the specific refactor that is step 1.

## Git and pull requests

- The canonical repository for all pushes and pull requests is
  `Gummygamer/Transformers-Forged-To-Fight-Offline-Version`.
- Never create, target, or suggest a pull request for the `geamztheangrybirds727` fork.
- Before creating a pull request, verify that the repository remote resolves to the
  `Gummygamer` repository.
