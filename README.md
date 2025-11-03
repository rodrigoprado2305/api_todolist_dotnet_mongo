# ToDoList API (.NET 9) — MongoDB Edition

Esta versão troca MySQL + Dapper por **MongoDB.Driver**, mantendo as mesmas rotas:

- `POST /tasks`  — cria (Id sequencial, simulando AUTO_INCREMENT)
- `GET /tasks`   — lista
- `GET /tasks/{id:int}` — busca por Id
- `PUT /tasks/{id:int}` — atualiza
- `DELETE /tasks/{id:int}` — apaga

## Configuração

- **SRV** (padrão neste app):  
  `MONGODB_URI=mongodb+srv://rodrigoprado:123@cluster0.jqwsmpj.mongodb.net/?retryWrites=true&w=majority&appName=Cluster0`  
  **DB**: `todo_db`

Você pode mudar por variável de ambiente: `MONGODB_URI` e `MONGODB_DB`.

## AutoMigrate
Na inicialização a API:
1. Aplica **JSON Schema** (campos fixos: `Id:int`, `Title:string(<=200)`, `Description:string|null`, `Completed:bool`, sem extras).
2. Garante `counters` e alinha `seq` com o maior `Id` existente.
3. Cria índice **único** em `tasks.Id`.

## Rodando
```bash
dotnet clean
dotnet restore
dotnet build
dotnet run --project src/Api/Api.csproj
```
## Rodando net 8.0
<TargetFramework>net8.0</TargetFramework>
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.0.11" />

## Rodando net 9.0
<TargetFramework>net9.0</TargetFramework>
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="9.0.0" />

## Rodando bash - Para usar um profile do launchSettings.json:
```
dotnet run --project D:\github\Rod\dotnet\api_todolist_dotnet_mongo\src\Api\Api.csproj --urls http://localhost:5035
dotnet run --project D:\github\Rod\dotnet\api_todolist_dotnet_mongo\src\Api\Api.csproj --launch-profile "Api"
```

## Login
`POST /login` — se definir `JWT_DEMO_USER` e `JWT_DEMO_PASS`, usa essas credenciais; senão, aceita qualquer par (apenas para estudo).

# Swagger

http://localhost:5035/swagger

## Rotas
- `POST /login` → retorna `{ "token": "..." }` (JWT HS256).  
  
http://localhost:5035/login
body: {
  "username": "rodrigo",
  "password": "vini123"
}

## Observações
- `_id` (ObjectId) é interno do Mongo; o `Id` inteiro é o que a API usa nas rotas.
- Depois, troque a senha do SRV — aqui está 123 só para **estudos**.
