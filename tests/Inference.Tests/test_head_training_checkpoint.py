"""One tiny CUDA optimizer continuation check, not model-training or accuracy evidence."""
import random
import tempfile
import unittest
from pathlib import Path

import head_training_checkpoint as checkpoint


class CheckpointTest(unittest.TestCase):
    def test_cuda_continuation_preserves_the_next_update_and_rejects_changed_contract(self):
        import torch

        if not torch.cuda.is_available():
            self.skipTest("CUDA required; a skip is not continuation evidence")
        torch.set_num_threads(1)
        torch.manual_seed(123)
        torch.cuda.manual_seed_all(123)
        rng = random.Random(123)
        model = torch.nn.Linear(4, 2).cuda()
        opt = torch.optim.AdamW(model.parameters(), lr=1e-4)
        def step():
            order = list(range(4))
            rng.shuffle(order)
            x = torch.randn(4, 4, device="cuda")[order]
            opt.zero_grad(set_to_none=True)
            loss = model(x).square().sum()
            loss.backward()
            opt.step()
            return loss.item()
        first = step()
        with tempfile.TemporaryDirectory() as root:
            contract = {"mode": "cuda-unit-fixture", "updates": 2}
            path = Path(checkpoint.save(torch, root, model, opt, rng, [first], contract))
            expected_loss = step()
            expected = {k: v.detach().clone() for k, v in model.state_dict().items()}
            loaded = checkpoint.restore(torch, path, model, opt, rng, contract)
            self.assertEqual(loaded, [first])
            self.assertEqual(step(), expected_loss)
            for key, value in model.state_dict().items():
                self.assertTrue(torch.equal(value, expected[key]))
            with self.assertRaises(ValueError):
                checkpoint.restore(torch, path, model, opt, rng, {"mode": "changed"})
            with path.open("ab") as file:
                file.write(b"changed")
            with self.assertRaises(ValueError):
                checkpoint.restore(torch, path, model, opt, rng, contract)


if __name__ == "__main__":
    unittest.main()
