from conftest import implementation


async def test_slow_subscriber_is_detached_without_blocking_producer():
    hub = implementation("events").EventHub(queue_size=2)
    async with hub.subscribe("fixture") as queue:
        for _ in range(20):
            hub.publish("fixture")
        assert queue.qsize() <= 2
        assert await queue.get() is False
        assert hub.slow_subscribers == 1
    assert hub.subscriber_count == 0
