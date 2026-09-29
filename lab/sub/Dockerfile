FROM python:3.12-slim

LABEL joy.hypervisor.lab="1" \
      joy.hypervisor.role="sub"

WORKDIR /opt/hvchan
COPY channel_server.py marker.sh ./
RUN sed -i 's/\r$//' marker.sh \
    && chmod +x marker.sh \
    && apt-get update \
    && apt-get install -y --no-install-recommends procps \
    && rm -rf /var/lib/apt/lists/*

EXPOSE 19800
CMD ["./marker.sh"]
