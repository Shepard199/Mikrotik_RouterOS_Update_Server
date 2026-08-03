#!/bin/bash
# IP Whitelist Testing Examples
# Run these commands to test IP whitelist functionality

echo "========================================"
echo "IP Whitelist Testing Examples"
echo "========================================"
echo ""

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

API_URL="http://localhost:5000"
HEALTH_URL="http://localhost:5000/health"

echo -e "${BLUE}Test 1: Health Check (Should always work - excluded endpoint)${NC}"
echo "Command: curl -i $HEALTH_URL"
curl -i $HEALTH_URL
echo ""
echo ""

echo -e "${BLUE}Test 2: API Access with Allowed IP${NC}"
echo "Command: curl -i -H 'X-Forwarded-For: 10.0.0.10' $API_URL/api/status"
curl -i -H 'X-Forwarded-For: 10.0.0.10' $API_URL/api/status
echo ""
echo ""

echo -e "${BLUE}Test 3: API Access with Denied IP${NC}"
echo "Command: curl -i -H 'X-Forwarded-For: 203.0.113.99' $API_URL/api/status"
curl -i -H 'X-Forwarded-For: 203.0.113.99' $API_URL/api/status
echo "Expected: 403 Forbidden"
echo ""
echo ""

echo -e "${BLUE}Test 4: API Access with Allowed CIDR Range${NC}"
echo "Command: curl -i -H 'X-Forwarded-For: 10.0.5.50' $API_URL/api/logs"
curl -i -H 'X-Forwarded-For: 10.0.5.50' $API_URL/api/logs
echo ""
echo ""

echo -e "${BLUE}Test 5: Localhost Access (Should work - allowed)${NC}"
echo "Command: curl -i http://127.0.0.1:5000/api/status"
curl -i http://127.0.0.1:5000/api/status
echo ""
echo ""

echo -e "${BLUE}Test 6: IPv6 Localhost (Should work)${NC}"
echo "Command: curl -i http://[::1]:5000/api/status"
curl -i http://[::1]:5000/api/status
echo ""
echo ""

echo -e "${BLUE}Test 7: X-Real-IP Header (Reverse Proxy Support)${NC}"
echo "Command: curl -i -H 'X-Real-IP: 192.168.1.10' $API_URL/api/status"
curl -i -H 'X-Real-IP: 192.168.1.10' $API_URL/api/status
echo ""
echo ""

echo -e "${BLUE}Test 8: Multiple X-Forwarded-For IPs (Should use first)${NC}"
echo "Command: curl -i -H 'X-Forwarded-For: 10.0.0.10, 203.0.113.99' $API_URL/api/status"
curl -i -H 'X-Forwarded-For: 10.0.0.10, 203.0.113.99' $API_URL/api/status
echo ""
echo ""

echo -e "${BLUE}Test 9: Private Network Range${NC}"
echo "Command: curl -i -H 'X-Forwarded-For: 192.168.1.100' $API_URL/api/status"
curl -i -H 'X-Forwarded-For: 192.168.1.100' $API_URL/api/status
echo ""
echo ""

echo -e "${BLUE}Test 10: Metrics Endpoint (Should be excluded)${NC}"
echo "Command: curl -i -H 'X-Forwarded-For: 203.0.113.99' $API_URL/metrics"
curl -i -H 'X-Forwarded-For: 203.0.113.99' $API_URL/metrics
echo ""
echo ""

echo "========================================"
echo "Tests Complete"
echo "========================================"
