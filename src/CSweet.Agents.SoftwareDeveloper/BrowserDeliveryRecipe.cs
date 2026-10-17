namespace CSweet.Agents.SoftwareDeveloper;

/// <summary>Small dependency-free serving recipe supplied as implementation guidance.</summary>
internal static class BrowserDeliveryRecipe
{
    internal const string Server = """
        import http from 'node:http';
        import {readFile, realpath} from 'node:fs/promises';
        import path from 'node:path';
        const root = await realpath(path.resolve('dist'));
        const types = {'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css; charset=utf-8','.json':'application/json','.png':'image/png','.svg':'image/svg+xml','.wasm':'application/wasm','.ogg':'audio/ogg','.mp3':'audio/mpeg'};
        const server = http.createServer(async (req,res) => {
          if (!['GET','HEAD'].includes(req.method)) {res.writeHead(405);res.end();return;}
          let pathname;
          try {pathname=decodeURIComponent(new URL(req.url,'http://localhost').pathname);} catch {res.writeHead(400);res.end();return;}
          if (pathname.includes('\0') || pathname.includes('\\')) {res.writeHead(400);res.end();return;}
          const target=path.resolve(root,'.'+(pathname==='/'?'/index.html':pathname));
          if (!target.startsWith(root+path.sep)) {res.writeHead(404);res.end();return;}
          try {
            const resolved=await realpath(target);
            if (!resolved.startsWith(root+path.sep)) {res.writeHead(404);res.end();return;}
            const body=await readFile(resolved);
            res.writeHead(200,{'Content-Type':types[path.extname(resolved)]??'application/octet-stream','Content-Length':body.length});
            res.end(req.method==='HEAD'?undefined:body);
          } catch {res.writeHead(404);res.end();}
        });
        server.listen(Number(process.env.PORT??8080),'0.0.0.0');
        for (const signal of ['SIGTERM','SIGINT']) process.on(signal,()=>server.close(()=>process.exit(0)));
        """;

    internal const string Dockerfile = """
        FROM node:22-bookworm-slim AS build
        WORKDIR /app
        COPY package.json package-lock.json ./
        RUN npm ci --no-audit --no-fund
        COPY . .
        RUN npm run build
        FROM node:22-bookworm-slim
        WORKDIR /app
        ENV NODE_ENV=production
        COPY --from=build /app/dist ./dist
        COPY server/serve.mjs ./server/serve.mjs
        USER node
        EXPOSE 8080
        CMD ["node","server/serve.mjs"]
        """;

    internal static string Guidance => "For a Node browser application, adapt this standard server/serve.mjs and Dockerfile recipe. " +
        "Use the project's approved Node version. Test /, a real asset, a missing asset, malformed URL encoding, and browser scene behavior. HTTP availability does not replace independent QA.\nServer:\n" + Server + "\nDockerfile:\n" + Dockerfile;
}
