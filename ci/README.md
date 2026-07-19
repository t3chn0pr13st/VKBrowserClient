# CI / публикация (шаблоны workflow)

Эти файлы — готовые GitHub Actions workflow. Они лежат здесь, а не в `.github/workflows/`,
потому что первоначальный пуш делался токеном без scope `workflow`.

- `ci.yml` — сборка на push/PR в `main`.
- `nuget.yml` — публикация приватного пакета в GitHub Packages по тегу `vX.Y.Z`.

## Активация (один раз)

```bash
# 1) выдать токену gh право на workflow-файлы
gh auth refresh -h github.com -s workflow

# 2) перенести файлы на штатное место и запушить
mkdir -p .github/workflows
git mv ci/ci.yml .github/workflows/ci.yml
git mv ci/nuget.yml .github/workflows/nuget.yml
git commit -m "ci: activate GitHub Actions workflows"
git push
```

После этого CI запустится на push, а публикация пакета — по пушу тега версии:

```bash
git tag v0.2.0 && git push origin v0.2.0
```
