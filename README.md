# 🍃🍒 CherryPick: For all your component picking needs!

[![CI](https://github.com/esnya/CherryPick/actions/workflows/ci.yml/badge.svg)](https://github.com/esnya/CherryPick/actions/workflows/ci.yml)

CherryPick is a no-nonsense, straight-to-the-point component searcher mod for [Resonite](https://resonite.com) via [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader).

With CherryPick, your component browser gains a new element at the top: a search bar!

<img src="image.png" width="400">

Simply start typing the name of the component you're looking for and the top ten results will appear immediately!

Recent choices are shown before fuzzy matches. Component and ProtoFlux choices are tracked separately, so switching browsers does not mix the two histories.

<img src="Resonite_pm9oFaDfHo.gif" width="250">

CherryPick also supports concrete generic typing. Add one or more type arguments to a node name, including nested collection types:

```text
ValueInput<string>
GetAtObject<IList<string>,string>
GetObjectWithObjectKey<IDictionary<string,Uri>,string,Uri>
```

Collection nodes also support a shorthand that infers their leading collection interface from the remaining type arguments. For example, `GetAtObject<string>` resolves to `GetAtObject<IList<string>,string>`.

<img src="Resonite_v4HSr3GShH.gif" width=350>

## Fork differences

This maintained fork of [BlueCyro/CherryPick](https://github.com/BlueCyro/CherryPick) adds:

- Separate recent-item lists for Components and ProtoFlux, shown before fuzzy matches.
- Expanded generic search and candidate coverage: partial type names, multiple and nested arguments, broader reference/value/collection candidates, and collection-interface inference.
- Optional non-persistent search windows.
- Optional ResoniteHotReloadLib support for local development, plus CI-built DLLs attached to tagged releases.

## Installation

1. Install [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader).
2. Download [CherryPick.dll](https://github.com/esnya/CherryPick/releases/latest/download/CherryPick.dll) and place it in the `rml_mods` directory.
3. Start Resonite.
