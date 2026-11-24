FROM node:20-alpine

WORKDIR /app

COPY mcp/ ./mcp/

# Introspection & runtime over stdio
CMD ["node", "mcp/bin/vacumify-mcp.js"]
