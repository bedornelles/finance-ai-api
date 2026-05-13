using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RegistrAi.Api.Data;
using RegistrAi.Api.Models;

namespace RegistrAi.Api.Controllers
{
    // Rota padrão: localhost:porta/api/transacoes
    [Route("api/[controller]")]
    [ApiController]
    public class TransacoesController : ControllerBase
    {
        private readonly AppDbContext _context;

        // O construtor onde o Controller "conhece" o banco de dados
        public TransacoesController(AppDbContext context)
        {
            _context = context;
        }

        // 1. Endpoint para CRIAR uma nova transação (POST)
        [HttpPost]
        public async Task<IActionResult> Criar(Transacao transacao)
        {
            _context.Transacoes.Add(transacao); // Prepara para salvar
            await _context.SaveChangesAsync(); // Efetiva no banco de dados

            return Ok(transacao); // Retorna sucesso (Status 200) com os dados
        }

        // 2. Endpoint para LISTAR todas as transações (GET)
        [HttpGet]
        public async Task<IActionResult> Listar()
        {
            var transacoes = await _context.Transacoes.ToListAsync(); // Puxa tudo do banco
            return Ok(transacoes); // Devolve a lista (Status 200)
        }

        // 3. Endpoint para BUSCAR UMA transação pelo ID (GET)
        [HttpGet("{id}")]
        public async Task<IActionResult> BuscarPorId(int id)
        {
            var transacao = await _context.Transacoes.FindAsync(id);
            if (transacao == null) return NotFound(); // Retorna 404 se não achar
            return Ok(transacao);
        }

        // 4. Endpoint para ATUALIZAR uma transação (PUT)
        [HttpPut("{id}")]
        public async Task<IActionResult> Atualizar(int id, Transacao transacaoAtualizada)
        {
            if (id != transacaoAtualizada.Id) return BadRequest(); // Retorna 400 se os IDs não baterem

            _context.Entry(transacaoAtualizada).State = EntityState.Modified;
            await _context.SaveChangesAsync();

            return NoContent(); // Retorna 204 (Deu certo e não tem nada para mostrar)
        }

        // 5. Endpoint para DELETAR uma transação (DELETE)
        [HttpDelete("{id}")]
        public async Task<IActionResult> Deletar(int id)
        {
            var transacao = await _context.Transacoes.FindAsync(id);
            if (transacao == null) return NotFound();

            _context.Transacoes.Remove(transacao);
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}