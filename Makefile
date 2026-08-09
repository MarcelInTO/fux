# fux — cross-platform terminal XML editor
#
#   make               build a self-contained single-file binary for this host -> bin/fux
#   make install       install it to $(PREFIX)/bin        (default /usr/local; may need sudo)
#   make uninstall     remove the installed binary
#   make run FILE=x    build + run the TUI on a file      (default: a sample doc)
#   make dump FILE=x   headless structure dump of a file
#   make smoke         headless engine build + XSD-validation check
#   make clean         remove build outputs
#   make help          list targets
#
# Overrides: PREFIX (install prefix), DESTDIR (staging root), CONFIG (Debug/Release),
#            RID (runtime id, auto-detected), FILE (input document).

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
.PHONY: build install uninstall run dump smoke clean help

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
drill:
	@script -q /dev/null dotnet run --project src/Fux -c $(CONFIG) -- --drill "$(FILE)" > /tmp/fux-drill.out 2>&1; \
	sed 's/\x1b\[[0-9;?]*[a-zA-Z]//g' /tmp/fux-drill.out | grep -E '^\s*\[(ok|FAIL)\]|DRILL:'; \
	grep -q "DRILL: PASS" /tmp/fux-drill.out

## smoke: headless engine build + XSD-validation check
smoke:
	dotnet run --project sandbox/smoke -c $(CONFIG) -- "$(FILE)"

## clean: remove build outputs
clean:
	rm -rf $(BIN)
	@dotnet clean $(PROJECT) -c $(CONFIG) >/dev/null 2>&1 || true
	@echo "fux: cleaned"

## help: list targets
help:
	@grep -E '^## ' $(MAKEFILE_LIST) | sed 's/^## /  /'
