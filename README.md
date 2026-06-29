# SM_AX_leader

Training and reference materials for the SM AX leader work folder.

## Contents

- `data/` contains spreadsheets, PDFs, HWPX files, HTML examples, images, CSV data, and supporting practice materials.
- A Windows Forms sample project is included under `data/` and targets `.NET 9` with `net9.0-windows`.

## GitHub Notes

This repository is prepared for GitHub with:

- `.gitignore` rules for local workspace metadata, .NET build output, Python cache files, and generated executables.
- `.gitattributes` rules so Office, PDF, HWPX, image, and archive files are treated as binary files.

Generated build folders such as `bin/` and `obj/` are intentionally ignored. Executable files are also ignored because GitHub blocks files larger than 100 MB, and this folder currently contains a generated `.exe` above that limit.

## First Push

After creating an empty repository on GitHub, connect this local folder with:

```bash
GIT_DIR=.git-local GIT_WORK_TREE=. git remote add origin <your-github-repository-url>
GIT_DIR=.git-local GIT_WORK_TREE=. git push -u origin main
```

This workspace has an empty read-only `.git/` placeholder, so the local Git metadata is stored in `.git-local/`. Use the same `GIT_DIR=.git-local GIT_WORK_TREE=.` prefix for local Git commands in this folder.
