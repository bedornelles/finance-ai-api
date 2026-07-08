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

         private string DispositivoIdAtual => Request.Headers["X-Device-Id"].ToString();

        // 1. Endpoint para CRIAR uma nova transação (POST)
        [HttpPost]
        public async Task<IActionResult> Criar(Transacao transacao)
        {
            if (string.IsNullOrWhiteSpace(DispositivoIdAtual))
                return BadRequest("Cabeçalho X-Device-Id é obrigatório.");

            // Normaliza para sempre salvar "Receita" ou "Despesa" com maiúscula correta,
            // independente do que o Flutter mandar ("despesa", "DESPESA", "despEsa"...)
            var tipoNormalizado = transacao.Tipo?.Trim().ToLower();

            if (tipoNormalizado != "receita" && tipoNormalizado != "despesa")
                return BadRequest("O tipo deve ser 'Receita' ou 'Despesa'.");

            transacao.DispositivoId = DispositivoIdAtual;

            _context.Transacoes.Add(transacao); // Prepara para salvar
            await _context.SaveChangesAsync(); // Efetiva no banco de dados

            return Ok(transacao); // Retorna sucesso (Status 200) com os dados
        }

        // 2. Endpoint para LISTAR todas as transações (GET)
        [HttpGet]
        public async Task<IActionResult> Listar()
        {
            var transacoes = await _context.Transacoes
                .Where(t => t.DispositivoId == DispositivoIdAtual)
                .ToListAsync(); // Puxa tudo do banco
            return Ok(transacoes); // Devolve a lista (Status 200)
        }

        // 3. Endpoint para BUSCAR UMA transação pelo ID (GET)
        [HttpGet("{id}")]
        public async Task<IActionResult> BuscarPorId(int id)
        {
            var transacao = await _context.Transacoes
                .FirstOrDefaultAsync(t => t.Id == id && t.DispositivoId == DispositivoIdAtual);
            if (transacao == null) return NotFound();
            return Ok(transacao);
        }

        // 4. Endpoint para ATUALIZAR uma transação (PUT)
        [HttpPut("{id}")]
        public async Task<IActionResult> Atualizar(int id, Transacao transacaoAtualizada)
        {
            if (id != transacaoAtualizada.Id) return BadRequest(); // Retorna 400 se os IDs não baterem

            var transacaoExistente = await _context.Transacoes
                .FirstOrDefaultAsync(t => t.Id == id && t.DispositivoId == DispositivoIdAtual);
            if (transacaoExistente == null) return NotFound();

            transacaoAtualizada.DispositivoId = DispositivoIdAtual;
            _context.Entry(transacaoExistente).CurrentValues.SetValues(transacaoAtualizada);
            await _context.SaveChangesAsync();

            return NoContent(); // Retorna 204 (Deu certo e não tem nada para mostrar)
        }

        // 5. Endpoint para DELETAR uma transação (DELETE)
        [HttpDelete("{id}")]
        public async Task<IActionResult> Deletar(int id)
        {
            var transacao = await _context.Transacoes
                .FirstOrDefaultAsync(t => t.Id == id && t.DispositivoId == DispositivoIdAtual);
            if (transacao == null) return NotFound();

            _context.Transacoes.Remove(transacao);
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}