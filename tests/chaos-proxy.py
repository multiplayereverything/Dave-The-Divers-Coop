"""UDP chaos proxy for lab runs: the client sends to LISTEN_PORT, the proxy forwards to the host and back, adding
one-way lag (plus jitter) and random loss in both directions.

    python -I tests/chaos-proxy.py 27878 127.0.0.1 27877 --lag-ms 120 --jitter-ms 20 --loss 0.05

Prints a summary line every 10 s: packets forwarded and dropped per direction.
"""
import argparse
import heapq
import random
import select
import socket
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("listen_port", type=int)
    parser.add_argument("host_ip")
    parser.add_argument("host_port", type=int)
    parser.add_argument("--lag-ms", type=float, default=100)
    parser.add_argument("--jitter-ms", type=float, default=20)
    parser.add_argument("--loss", type=float, default=0.05)
    parser.add_argument("--seed", type=int, default=7)
    args = parser.parse_args()
    rng = random.Random(args.seed)

    front = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    front.bind(("127.0.0.1", args.listen_port))
    back = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    back.bind(("127.0.0.1", 0))
    host = (args.host_ip, args.host_port)
    client = None
    queue = []  # (due, seq, socket, data, address)
    seq = 0
    stats = {"up": 0, "down": 0, "up_drop": 0, "down_drop": 0}
    next_report = time.time() + 10

    while True:
        now = time.time()
        timeout = max(0.0, min(0.01, queue[0][0] - now)) if queue else 0.01
        readable, _, _ = select.select([front, back], [], [], timeout)
        for sock in readable:
            data, address = sock.recvfrom(65535)
            if sock is front:
                client = address
                direction, out, target = "up", back, host
            else:
                if client is None:
                    continue
                direction, out, target = "down", front, client
            if rng.random() < args.loss:
                stats[direction + "_drop"] += 1
                continue
            stats[direction] += 1
            due = now + (args.lag_ms + rng.uniform(-args.jitter_ms, args.jitter_ms)) / 1000.0
            seq += 1
            heapq.heappush(queue, (due, seq, out, data, target))
        now = time.time()
        while queue and queue[0][0] <= now:
            _, _, out, data, target = heapq.heappop(queue)
            out.sendto(data, target)
        if now >= next_report:
            next_report = now + 10
            print("chaos", stats, flush=True)


if __name__ == "__main__":
    main()
