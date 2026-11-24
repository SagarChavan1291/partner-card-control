#!/usr/bin/env bash
# Plays the partner: gets an access token, encrypts a PAN, locks and unlocks a card, then shows the
# card platform's audit trail. Needs the identity provider, both stubs and the API running (see README).
# Usage, from the repository root:  ./scripts/sample-call.sh [PAN] [CORPORATE_ID]
set -euo pipefail

PAN="${1:-4111111111111111}"
CORPORATE_ID="${2:-CORP-1001}"
INITIATING_USER="${INITIATING_USER:-jane.smith@acme.example}"
API="${API:-http://localhost:5190}"

if [ ! -f .dev-keys/bank-dev-1.public.pem ]; then
  echo "Creating the bank dev key pair (.dev-keys/)..."
  dotnet run tools/jwe-tool.cs -- keygen > /dev/null
fi

# No jq needed: pull access_token out of the JSON with sed.
TOKEN=$(curl -sf -X POST http://localhost:5090/oauth2/token \
  -d grant_type=client_credentials \
  -d client_id=partner-app \
  -d client_secret=dev-only-partner-app-secret \
  -d scope=api://card-control/.default | sed -E 's/.*"access_token":"([^"]+)".*/\1/')

PAN_JWE=$(dotnet run tools/jwe-tool.cs -- encrypt --key .dev-keys/bank-dev-1.public.pem "$PAN")

card_action() {
  echo
  echo "POST /v1/cards/$1"
  curl -s -X POST "$API/v1/cards/$1" \
    -H "Authorization: Bearer $TOKEN" \
    -H "X-Initiating-User: $INITIATING_USER" \
    -H 'Content-Type: application/json' \
    -d "{\"encryptedPan\":\"$PAN_JWE\",\"corporateId\":\"$CORPORATE_ID\"}" \
    -w '\n-> HTTP %{http_code}\n'
}

card_action lock
card_action unlock

echo
echo "Card platform audit trail (all cards):"
curl -s http://localhost:5080/admin/audit
echo
