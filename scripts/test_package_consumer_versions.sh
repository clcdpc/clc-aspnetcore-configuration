#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="$repository_root/tests/Clc.Configuration.Tests/Clc.Configuration.Tests.csproj"
configuration="${BUILD_CONFIGURATION:-Release}"
smoke_test="FullyQualifiedName~Clc.Configuration.Tests.PackageConsumerTests.PackedNuGetPackageLoadsHostlessFixturesAndStartsAnIsolatedConsumer"
explicit_version="${PACKAGE_SMOKE_EXPLICIT_VERSION:-9.8.7-smoke.4}"
ci_version="${PACKAGE_VERSION:-0.0.0-ci.431.2}"
log_file="$(mktemp)"
trap 'rm -f "$log_file"' EXIT

run_smoke_test() {
  local label="$1"
  local expected_version="$2"
  shift
  shift
  printf 'Running packed-consumer smoke test with %s.\n' "$label"
  env -u PACKAGE_VERSION \
    "CLC_CONFIGURATION_PACKAGE_SMOKE_EXPECTED_VERSION=$expected_version" \
    dotnet test "$test_project" \
    --configuration "$configuration" \
    --filter "$smoke_test" \
    --verbosity minimal \
    "$@"
}

run_smoke_test "the local fallback" "0.0.0-local"
run_smoke_test "explicit Version and PackageVersion $explicit_version" "$explicit_version" \
  "-p:Version=$explicit_version" \
  "-p:PackageVersion=$explicit_version"

printf 'Running packed-consumer smoke test with the CI-supplied version %s.\n' "$ci_version"
env \
  "PACKAGE_VERSION=$ci_version" \
  "CLC_CONFIGURATION_PACKAGE_SMOKE_EXPECTED_VERSION=$ci_version" \
  dotnet test "$test_project" \
  --configuration "$configuration" \
  --filter "$smoke_test" \
  --verbosity minimal \
  "-p:Version=$ci_version" \
  "-p:PackageVersion=$ci_version"

mismatched_package_version="${explicit_version}-different"
printf 'Checking that disagreeing Version and PackageVersion properties fail explicitly.\n'
if env -u PACKAGE_VERSION dotnet test "$test_project" \
  --configuration "$configuration" \
  --filter "$smoke_test" \
  --verbosity minimal \
  "-p:Version=$explicit_version" \
  "-p:PackageVersion=$mismatched_package_version" >"$log_file" 2>&1; then
  cat "$log_file"
  echo "Expected the package smoke test to reject disagreeing Version and PackageVersion properties." >&2
  exit 1
fi

if ! grep -F "requires the evaluated MSBuild properties Version and PackageVersion to match" "$log_file" >/dev/null; then
  cat "$log_file"
  echo "The package smoke test failed without reporting the Version/PackageVersion mismatch." >&2
  exit 1
fi

echo "Packed-consumer package version checks passed."
