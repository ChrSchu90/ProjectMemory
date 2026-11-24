#!/usr/bin/env bash
set -euo pipefail

skills_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dist_dir="$skills_dir/dist"
temporary_dir="$(mktemp -d)"
trap 'rm -rf -- "$temporary_dir"' EXIT

for tool in zip unzip; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "Missing required command: $tool" >&2
        exit 1
    fi
done

mkdir -p "$temporary_dir/archives"

clients=(chatgpt-web codex claude-web claude-code hermes)
for client in "${clients[@]}"; do
    source_dir="$skills_dir/clients/$client"
    package_root="$temporary_dir/$client"
    package_dir="$package_root/project-memory"
    archive="ProjectMemory-skill-$client.zip"

    if [[ ! -f "$source_dir/SKILL.md" ]]; then
        echo "Missing skill entrypoint: $source_dir/SKILL.md" >&2
        exit 1
    fi

    mkdir -p "$package_dir"
    cp -R "$skills_dir/common/references" "$package_dir/references"
    sed 's#../../common/references/#references/#g' "$source_dir/SKILL.md" > "$package_dir/SKILL.md"

    if [[ -d "$source_dir/instructions" ]]; then
        cp -R "$source_dir/instructions" "$package_dir/instructions"
    fi

    if [[ -d "$source_dir/agents" ]]; then
        cp -R "$source_dir/agents" "$package_dir/agents"
        mkdir -p "$package_dir/assets"
        cp "$skills_dir/common/assets/icon.svg" "$package_dir/assets/icon.svg"
    fi

    if grep -Fq '../../common/' "$package_dir/SKILL.md"; then
        echo "Unresolved repository-relative reference in $client/SKILL.md" >&2
        exit 1
    fi

    while IFS= read -r reference; do
        if [[ ! -f "$package_dir/$reference" ]]; then
            echo "Missing packaged reference for $client: $reference" >&2
            exit 1
        fi
    done < <(grep -oE 'references/[a-z0-9-]+\.md' "$package_dir/SKILL.md" | sort -u)

    (cd "$package_root" && zip -q -r "$temporary_dir/archives/$archive" project-memory)
    unzip -tqq "$temporary_dir/archives/$archive"
done

mkdir -p "$dist_dir"
for client in "${clients[@]}"; do
    archive="ProjectMemory-skill-$client.zip"
    mv -f "$temporary_dir/archives/$archive" "$dist_dir/$archive"
    echo "Created skills/dist/$archive"
done
