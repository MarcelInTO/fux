# fux — cross-platform terminal XML editor
#
#   make               build a self-contained single-file binary for this host -> bin/fux
#   make install       install it to $(PREFIX)/bin        (default /usr/local; may need sudo)
#   make uninstall     remove the installed binary
#   make run FILE=x    build + run the TUI on a file      (default: a sample doc)
#   make dump FILE=x   headless structure dump of a file
#   make smoke         headless engine build + XSD-validation check
#   make release V=..  tag main and push it, publishing a release (VERSION=0.2.3)
#   make clean         remove build outputs
#   make help          list targets
#
# Overrides: PREFIX (install prefix), DESTDIR (staging root), CONFIG (Debug/Release),
#            RID (runtime id, auto-detected), FILE (input document),
#            VERSION (release version, no leading v), FORCE (skip the release CI check).

PROJECT := src/Fux/Fux.csproj
SMOKE   := sandbox/smoke/smoke.csproj
CONFIG  ?= Release
BIN     := bin
EXE     := $(BIN)/fux
PREFIX  ?= /usr/local
DESTDIR ?=
FILE    ?= sandbox/testdata/emp.xml

# Detect the host runtime identifier for a native, self-contained build.
UNAME_S := $(shell uname -s)
UNAME_M := $(shell uname -m)
ifeq ($(UNAME_S),Darwin)
  ifeq ($(UNAME_M),arm64)
    RID ?= osx-arm64
  else
    RID ?= osx-x64
  endif
else ifeq ($(UNAME_S),Linux)
  ifeq ($(UNAME_M),aarch64)
    RID ?= linux-arm64
  else
    RID ?= linux-x64
  endif
endif

.DEFAULT_GOAL := build
.PHONY: build install uninstall run dump drill smoke release clean help

## build: self-contained single-file binary for this host -> bin/fux
build:
	@test -n "$(RID)" || { echo "fux: unsupported host $(UNAME_S)/$(UNAME_M); pass RID=..."; exit 1; }
	dotnet publish $(PROJECT) -c $(CONFIG) -r $(RID) --self-contained true -p:PublishSingleFile=true -o $(BIN)
	@echo "fux: built $(EXE) ($(RID))"

## install: copy the binary to $(PREFIX)/bin (may need sudo)
install: build
	install -d "$(DESTDIR)$(PREFIX)/bin"
	install -m 0755 "$(EXE)" "$(DESTDIR)$(PREFIX)/bin/fux"
	@echo "fux: installed $(DESTDIR)$(PREFIX)/bin/fux"

## uninstall: remove the installed binary
uninstall:
	rm -f "$(DESTDIR)$(PREFIX)/bin/fux"
	@echo "fux: removed $(DESTDIR)$(PREFIX)/bin/fux"

## run: build + run the TUI on FILE
run:
	dotnet run --project src/Fux -c $(CONFIG) -- "$(FILE)"

## dump: headless structure dump of FILE
dump:
	dotnet run --project src/Fux -c $(CONFIG) -- --dump "$(FILE)"

## drill: headless interactive self-test of the TUI (key injection + render assertions)
# The report goes to stderr and the TUI's repaints to stdout, and the PTY merges the two, so
# any check that follows a repaint starts mid-line. Anchoring the filter at '^' therefore hid
# 21 of 282 checks from the printed report — the pane-title guard, the quit-prompt guard and
# both nudge row counts among them. Pass/fail was never affected (that is the DRILL: PASS grep
# below), but a failure in one of those checks left no trace in the log, which is precisely
# when the log matters. Match anywhere in the line and print only the match.
#
# Do NOT "simplify" this by dropping script(1): with no TTY the drill runs 277 checks rather
# than 282, so the real terminal is exercising strictly more.
drill:
	@script -q /dev/null dotnet run --project src/Fux -c $(CONFIG) -- --drill "$(FILE)" > /tmp/fux-drill.out 2>&1; \
	sed 's/\x1b\[[0-9;?]*[a-zA-Z]//g' /tmp/fux-drill.out | grep -aoE '\[(ok|FAIL)\][^[:cntrl:]]*|DRILL: [^[:cntrl:]]*'; \
	grep -aq "DRILL: PASS" /tmp/fux-drill.out

## smoke: headless engine build + XSD-validation check
smoke:
	dotnet run --project sandbox/smoke -c $(CONFIG) -- "$(FILE)"

## release: tag VERSION on main and push it, which publishes a release
#
# Wraps the three commands a release actually needs — switch, pull, tag+push — behind the
# checks that make them safe to run without thinking. Each guard is here because of a way
# this has already gone wrong or nearly did:
#
#   * VERSION is validated against the same pattern release.yml accepts, so a typo fails
#     here rather than after five platforms have built.
#   * The tree must be clean and main must be current: a tag is a promise about a specific
#     commit, and tagging with uncommitted work names something nobody can reproduce.
#   * The tag must not already exist locally or on origin. Re-tagging is how you end up
#     wanting to move a published tag, which is the one thing you cannot do quietly.
#   * main's CI must be green. Tagging while CI is still in flight spends a version number
#     on a commit nobody has verified. Needs gh; skipped with a warning if absent, and
#     FORCE=1 overrides it deliberately.
#
# Every check runs before `git switch`, so a rejected release leaves you on the branch you
# started from. None of them depend on being on main: tags are not branch-scoped, ls-remote
# asks origin directly, and the CI query names the branch explicitly.
#
# The tag is annotated (-a), so it carries a date and an author of its own, and is pushed
# by name — never `--tags`, which would offer up every unrelated tag in the repository.
release:
	@test -n "$(VERSION)" || { echo "usage: make release VERSION=0.2.3"; exit 1; }
	@echo "$(VERSION)" | grep -qE '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$$' \
	  || { echo "fux: '$(VERSION)' is not X.Y.Z or X.Y.Z-prerelease"; exit 1; }
	@test -z "$$(git status --porcelain)" \
	  || { echo "fux: working tree is not clean; commit or stash first"; exit 1; }
	@git rev-parse -q --verify "refs/tags/v$(VERSION)" >/dev/null \
	  && { echo "fux: tag v$(VERSION) already exists locally"; exit 1; }; true
	@test -z "$$(git ls-remote --tags origin 'refs/tags/v$(VERSION)')" \
	  || { echo "fux: tag v$(VERSION) is already on origin"; exit 1; }
	@if [ -n "$(FORCE)" ]; then echo "fux: FORCE set, skipping the CI check"; \
	 elif command -v gh >/dev/null 2>&1; then \
	   c=$$(gh run list --branch main --workflow "fux CI" --limit 1 --json conclusion --jq '.[0].conclusion' 2>/dev/null); \
	   [ "$$c" = "success" ] || { echo "fux: main CI is '$$c', not success — wait for it, or FORCE=1"; exit 1; }; \
	   echo "fux: main CI is green"; \
	 else echo "fux: gh not found; skipping the CI check (FORCE=1 to silence)"; fi
	git switch main
	git pull --ff-only
	git tag -a "v$(VERSION)" -m "fux $(VERSION)"
	git push origin "v$(VERSION)"
	@echo "fux: pushed v$(VERSION); release.yml is building. Follow it with:"
	@echo "     gh run watch \$$(gh run list --workflow 'fux release' --limit 1 --json databaseId --jq '.[0].databaseId')"

## clean: remove build outputs
clean:
	rm -rf $(BIN)
	@dotnet clean $(PROJECT) -c $(CONFIG) >/dev/null 2>&1 || true
	@echo "fux: cleaned"

## help: list targets
help:
	@grep -E '^## ' $(MAKEFILE_LIST) | sed 's/^## /  /'
