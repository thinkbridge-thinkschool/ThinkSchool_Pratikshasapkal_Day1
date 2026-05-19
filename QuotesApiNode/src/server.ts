import http, {
    IncomingMessage,
    ServerResponse
} from "node:http";

import Database from "better-sqlite3";

const db = new Database("quotes.db");

db.exec(`
    CREATE TABLE IF NOT EXISTS quotes (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        author TEXT NOT NULL,
        text TEXT NOT NULL
    )
`);

const server = http.createServer((
    request: IncomingMessage,
    response: ServerResponse
) => {

    const url = new URL(
        request.url ?? "",
        `http://${request.headers.host}`
    );



    if (
        request.method === "GET" &&
        url.pathname === "/"
    ) {

        response.writeHead(200, {
            "Content-Type": "application/json"
        });

        response.end(JSON.stringify({
            message: "Quotes API Running"
        }));

        return;
    }



    if (
        request.method === "GET" &&
        url.pathname === "/api/quotes"
    ) {

        const page = Number(
            url.searchParams.get("page") ?? "1"
        );

        const size = Number(
            url.searchParams.get("size") ?? "10"
        );

        const quotes = db.prepare(`
            SELECT *
            FROM quotes
            ORDER BY id
            LIMIT ?
            OFFSET ?
        `).all(
            size,
            (page - 1) * size
        );

        response.writeHead(200, {
            "Content-Type": "application/json"
        });

        response.end(JSON.stringify(quotes));

        return;
    }



    if (
        request.method === "GET" &&
        url.pathname.startsWith("/api/quotes/")
    ) {

        const id = Number(
            url.pathname.split("/").pop()
        );

        const quote = db.prepare(`
            SELECT *
            FROM quotes
            WHERE id = ?
        `).get(id);

        if (!quote) {

            response.writeHead(404);

            response.end();

            return;
        }

        response.writeHead(200, {
            "Content-Type": "application/json"
        });

        response.end(JSON.stringify(quote));

        return;
    }



    if (
        request.method === "POST" &&
        url.pathname === "/api/quotes"
    ) {

        let body = "";

        request.on("data", chunk => {
            body += chunk.toString();
        });

        request.on("end", () => {

            const data = JSON.parse(body);

            const author = data.author;
            const text = data.text;

            const result = db.prepare(`
                INSERT INTO quotes (author, text)
                VALUES (?, ?)
            `).run(author, text);

            const quote = {
                id: result.lastInsertRowid,
                author,
                text
            };

            response.writeHead(201, {
                "Content-Type": "application/json"
            });

            response.end(JSON.stringify(quote));
        });

        return;
    }



    if (
        request.method === "DELETE" &&
        url.pathname.startsWith("/api/quotes/")
    ) {

        const id = Number(
            url.pathname.split("/").pop()
        );

        const result = db.prepare(`
            DELETE FROM quotes
            WHERE id = ?
        `).run(id);

        if (result.changes === 0) {

            response.writeHead(404);

            response.end();

            return;
        }

        response.writeHead(200, {
            "Content-Type": "application/json"
        });

        response.end(JSON.stringify({
            message: "Quote deleted successfully"
        }));

        return;
    }



    response.writeHead(404);

    response.end();
});

server.listen(3000, () => {
    console.log("Server running on http://localhost:3000");
});