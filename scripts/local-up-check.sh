#!/usr/bin/env bash
# Confere o caminho do scripts/local-up de ponta a ponta, com a MESMA configuração (compose.app.yml + .env.local):
#   1. roda o scripts/local-up.sh (sobe ou reaproveita a stack sigh-local);
#   2. confere no log da API que os dados de demonstração foram criados e que não há "Demo: falha";
#   3. confere que a saída do local-up lista o administrador do seed com os usuários de demonstração;
#   4. entra (POST /api/v1/auth/login, pelo web) com o administrador do seed, o admin.usga e o juridico, com a senha
#      de demonstração do .env.local.
# É o que o job "Local-up (stack de demonstração)" do CI roda (.github/workflows/local-up.yml). Não apaga nada:
# para zerar, scripts/local-reset.sh.
set -euo pipefail

cd "$(dirname "$0")/.."
ENV_FILE=".env.local"
PROJECT="${SIGH_LOCAL_PROJECT:-sigh-local}"
COMPOSE=(docker compose -p "$PROJECT" -f compose.app.yml --env-file "$ENV_FILE")
OUT="$(mktemp)"
trap 'rm -f "$OUT"' EXIT

scripts/local-up.sh 2>&1 | tee "$OUT"

value() { grep -E "^$1=" "$ENV_FILE" | head -1 | cut -d= -f2-; }
fail() { echo "FALHOU: $*" >&2; exit 1; }

logs="$("${COMPOSE[@]}" logs --no-color api 2>&1)"
if grep -q "Demo: falha" <<<"$logs"; then
  grep -A5 "Demo: falha" <<<"$logs" >&2
  fail "o log da API tem \"Demo: falha\""
fi
grep -q "Demo: dados de demonstração prontos" <<<"$logs" || fail "o log da API não confirma os dados de demonstração"
echo "ok: log da API com \"Demo: dados de demonstração prontos\" e sem \"Demo: falha\""

admin="$(value SIGH_ADMIN_EMAIL)"; admin="${admin:-admin@demo.local}"
grep -q "todos com a senha: $(value SIGH_DEMO_PASSWORD)" "$OUT" || fail "a saída do local-up não mostra a senha de demonstração"
grep -qE "^ +${admin//./\\.} +Administrador" "$OUT" || fail "a saída do local-up não lista o administrador $admin"
echo "ok: a saída do local-up lista $admin com a senha de demonstração"

port="$(value SIGH_WEB_PORT)"; port="${port:-8080}"
for email in "$admin" admin.usga@demo.local juridico@demo.local; do
  status="$(curl -s -o /dev/null -w '%{http_code}' -X POST "http://localhost:$port/api/v1/auth/login" \
    -H 'Content-Type: application/json' \
    --data "{\"email\":\"$email\",\"password\":\"$(value SIGH_DEMO_PASSWORD)\"}")"
  [ "$status" = "200" ] || fail "login de $email respondeu $status"
  echo "ok: login de $email (200)"
done
echo "Local-up conferido."
