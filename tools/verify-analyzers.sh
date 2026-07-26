#!/usr/bin/env bash
#
# Proves the four house analyzer packs actually EXECUTED in every compilation.
#
# BuildStandardsTests reads Directory.Build.props and .editorconfig, so it can only prove that
# nothing has been *configured* to stop the analyzers. It cannot prove one still runs, and the way
# an analyzer pack dies is silence: a pack that fails to load emits no diagnostic of its own, so the
# build stays green, reports zero warnings, and enforces nothing. `/p:ReportAnalyzer=true` prints
# every analyzer assembly that ran, per compilation, and this script fails when one is absent.
#
# It lives outside the test suite on purpose: the check needs a --no-incremental rebuild of the same
# assemblies the test host has loaded, which is not a thing to do from inside `dotnet test`. It is a
# CI step instead (.github/workflows/ci.yml), and cheap there — the whole solution rebuilds with the
# analyzer report on in about 6 s.
#
# Usage:
#   tools/verify-analyzers.sh              # build the solution, then check the report
#   tools/verify-analyzers.sh --log FILE   # check a report captured earlier (what the tests drive)

set -euo pipefail

cd "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# The house set, as `<package id>=<analyzer assembly name>`. The two halves differ for Roslynator,
# whose package ships an assembly under another name, which is exactly why this mapping is written
# down rather than inferred. AnalyzerExecutionCheckTests pins the package half against the
# PackageReference set in Directory.Build.props, so adding a fifth pack there fails until it is
# named here too.
EXPECTED_PACKS=(
  "StyleCop.Analyzers=StyleCop.Analyzers"
  "Roslynator.Analyzers=Roslynator.CSharp.Analyzers"
  "SonarAnalyzer.CSharp=SonarAnalyzer.CSharp"
  "Meziantou.Analyzer=Meziantou.Analyzer"
)

solution="DocuMe.slnx"
log=""

while [ $# -gt 0 ]; do
  case "$1" in
    --log)
      log="${2:-}"
      if [ -z "$log" ]; then
        echo "verify-analyzers: --log needs a file" >&2
        exit 2
      fi
      shift 2
      ;;
    *)
      echo "verify-analyzers: unknown argument '$1'" >&2
      exit 2
      ;;
  esac
done

if [ ! -f "$solution" ]; then
  echo "verify-analyzers: no $solution here — the script must sit in tools/ of the repo" >&2
  exit 2
fi

# Spelled as an `if` rather than `[ -n … ] && rm`, because the last command of an EXIT trap sets the
# script's exit status: with --log there is no scratch file, the test is false, and the short form
# turned a clean check into exit 1.
cleanup() {
  if [ -n "${scratch:-}" ]; then
    rm -f "$scratch"
  fi
}

trap cleanup EXIT

if [ -z "$log" ]; then
  scratch="$(mktemp -t docume-report-analyzer.XXXXXX)"
  log="$scratch"

  # -m:1 is load-bearing, not a leftover. With more than one MSBuild node the console logger
  # interleaves lines from projects building in parallel, and the report below is attributed to a
  # project by the `from project "..."` marker that precedes it. One node keeps that attribution
  # honest. The build is seconds either way.
  if ! dotnet build "$solution" --no-incremental -m:1 /p:ReportAnalyzer=true -v:d > "$log" 2>&1; then
    echo "verify-analyzers: the build failed, so there is no analyzer report to check." >&2
    tail -40 "$log" >&2
    exit 1
  fi
fi

if [ ! -f "$log" ]; then
  echo "verify-analyzers: no such log '$log'" >&2
  exit 2
fi

# Every project in the solution has to show up with a report of its own. Read from the solution
# rather than listed here, so a project added to the build is covered without touching this file.
projects="$(sed -n 's|.*<Project Path="\([^"]*\)".*|\1|p' "$solution" | sed 's|.*[/\\]||' | paste -sd, -)"

if [ -z "$projects" ]; then
  echo "verify-analyzers: $solution names no projects, so this check would pass vacuously" >&2
  exit 2
fi

awk \
  -v packs="$(printf '%s\n' "${EXPECTED_PACKS[@]}" | sed 's/^[^=]*=//' | paste -sd, -)" \
  -v projects="$projects" '
function basename(path,   cut) {
  cut = path
  # Two subs rather than one character class: a class holding an escaped slash inside a regex
  # literal is the corner of awk where implementations differ, and this has to run under whatever
  # awk the runner ships (mawk on ubuntu-latest, BWK awk on a Mac).
  sub(/.*\//, "", cut)
  sub(/.*\\/, "", cut)
  # MSBuild sometimes qualifies the path it names, e.g. `x.csproj::TargetFramework=net10.0`.
  sub(/\.csproj.+$/, ".csproj", cut)
  return cut
}

BEGIN {
  split(packs, want, ",")
  split(projects, need, ",")
  blocks = 0
  current = "(unknown project)"
}

# MSBuild names the project it is compiling ~20 lines above the report it prints.
/from project "/ {
  if (match($0, /from project "[^"]+"/)) {
    current = basename(substr($0, RSTART + 14, RLENGTH - 15))
  }
  next
}

/Total analyzer execution time:/ {
  blocks++
  owner[blocks] = current
  next
}

# One row of the report table per analyzer assembly. The per-rule rows underneath carry no
# `, Version=`, which is what separates the two without depending on indentation or on the decimal
# separator (the runner prints "4.6 seconds", a machine on a comma locale prints "4,6").
blocks > 0 && index($0, ", Version=") > 0 {
  if (match($0, /[A-Za-z][A-Za-z0-9_.]*, Version=/)) {
    ran[blocks SUBSEP substr($0, RSTART, RLENGTH - 10)] = 1
  }
}

END {
  status = 0

  if (blocks == 0) {
    print "FAIL: the log holds no analyzer report at all. Was /p:ReportAnalyzer=true dropped, or the verbosity lowered below -v:d?" > "/dev/stderr"
    exit 1
  }

  for (block = 1; block <= blocks; block++) {
    compiled[owner[block]] = 1
    missing = ""

    for (i in want) {
      if (!((block SUBSEP want[i]) in ran)) {
        missing = missing " " want[i]
      }
    }

    if (missing == "") {
      printf "ok   %s ran: %s\n", owner[block], packs
      continue
    }

    printf "FAIL %s: the compilation ran, these analyzers did not:%s\n", owner[block], missing > "/dev/stderr"
    status = 1
  }

  for (j in need) {
    if (!(need[j] in compiled)) {
      printf "FAIL %s: no analyzer report — it never compiled, so nothing analysed it.\n", need[j] > "/dev/stderr"
      status = 1
    }
  }

  if (status == 0) {
    printf "%d compilation(s) checked, every house analyzer pack ran in each.\n", blocks
  }

  exit status
}
' "$log"
