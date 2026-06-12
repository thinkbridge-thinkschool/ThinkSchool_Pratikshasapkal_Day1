import http from 'k6/http';
import { check } from 'k6';

export const options = {
    vus: 10,
    duration: '30s',
};

const BASE_URL = 'http://localhost:5032';

export default function () {
    const res = http.get(`${BASE_URL}/ef-authors-with-quotecount`);

    check(res, {
        'status is 200': (r) => r.status === 200,
    });
}