# Deterministic URL query builder
Implement the existing `buildQuery(params)` placeholder in `src/url.ts`. Return
an application/x-www-form-urlencoded query string without a leading question
mark. Sort own property keys with JavaScript's default string sort; serialize
string/number/boolean values with String(value). Omit only null and undefined;
include zero, false and empty strings. Use URLSearchParams encoding (spaces become
`+`, literal plus becomes `%2B`), including Unicode and reserved characters in
keys/values. Empty input yields empty string. Do not mutate params and preserve
`joinPath`. Run `node --experimental-strip-types smoke.ts`. No dependencies.
