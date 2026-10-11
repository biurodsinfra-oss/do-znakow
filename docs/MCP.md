# ZnakiAI przez MCP (Claude Desktop / Claude Code)

Claude na Twoim komputerze steruje wtyczką w otwartym BricsCAD. Nie potrzeba klucza API: planuje Claude z Twojego konta, a wtyczka liczy geometrię i rysuje.

```
Claude Desktop / Claude Code ── stdio ──> ZnakiAI.Mcp.exe ── named pipe ──> wtyczka ZnakiAI w BricsCAD
```

Pipe `ZnakiAI.Bricscad` jest lokalny i dostępny tylko dla bieżącego użytkownika Windows.

## Instalacja

1. Zbuduj i wczytaj wtyczkę jak w [README](../README.md). Przy wczytaniu w linii poleceń pojawi się `Most MCP: włączony.`
   Komenda `ZNAKIMCP` włącza i wyłącza most. `"mostMcp": false` w `znakiai.config.json` wyłącza go na stałe.
2. Zbuduj serwer MCP:
   ```
   dotnet publish src/ZnakiAI.Mcp -c Release -r win-x64 --self-contained false -o C:\ZnakiAI\mcp
   ```
3. Podłącz go do Claude:
   - **Claude Desktop**: Settings → Developer → Edit Config, w `claude_desktop_config.json` dopisz:
     ```json
     {
       "mcpServers": {
         "bricscad": { "command": "C:\\ZnakiAI\\mcp\\ZnakiAI.Mcp.exe" }
       }
     }
     ```
     i uruchom ponownie Claude Desktop.
   - **Claude Code**:
     ```
     claude mcp add bricscad -- C:\ZnakiAI\mcp\ZnakiAI.Mcp.exe
     ```

## Narzędzia

| Narzędzie | Działanie |
|---|---|
| `stan` | czy BricsCAD odpowiada, aktywny rysunek, wskazana oś |
| `zasady` | reguły projektowania i katalog kodów (ten sam tekst co dla `ZNAKIAI`) |
| `wskaz_os` | projektant wskazuje oś w BricsCAD (albo podaj uchwyt) |
| `kontekst` | długość osi i istniejące znaki w pobliżu |
| `km_punktu`, `punkt_na_km` | kontrola położeń |
| `sprawdz_plan` | walidacja planu bez rysowania: błędy, słupki, zestawienie |
| `wstaw_plan` | podgląd w BricsCAD i pytanie Tak/Nie; po akceptacji zapis jak w `ZNAKIPLAN` (CSV, tabela) |

Rysunek zmienia tylko `wstaw_plan`, zawsze po Twoim potwierdzeniu w BricsCAD. Cofnięcie: `U`.

Przykład polecenia w Claude: *„Wskaż oś w BricsCAD i zaprojektuj tymczasowe oznakowanie zajęcia prawego pasa od km 40 do 80.”*

## Gdy nie działa

- `Brak połączenia z BricsCAD`: BricsCAD nie działa, wtyczka nie jest wczytana albo most jest wyłączony (`ZNAKIMCP`).
- `BricsCAD nie odpowiedział`: w linii poleceń czeka inna komenda. Zakończ ją (Esc) i ponów.
- Kilka otwartych okien BricsCAD: most działa w tym, które pierwsze wczytało wtyczkę.
